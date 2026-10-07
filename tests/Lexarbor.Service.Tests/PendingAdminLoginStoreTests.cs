using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Lexarbor.Service.Tests;

public class PendingAdminLoginStoreTests
{
    [Fact]
    public void Create_GeneratesIndependentCanonicalMaterial_AndRevealsVerifierOnlyOnConsume()
    {
        var clock = new Clock();
        var store = new PendingAdminLoginStore(clock);
        var values = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var start = Assert.IsType<PendingAdminLoginStart>(store.Create(cancellationToken: TestContext.Current.CancellationToken));
            foreach (var value in new[] { start.State, start.Nonce, start.BrowserBinding, start.Challenge })
            {
                Assert.Equal(43, value.Length);
                Assert.All(value, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
                Assert.Equal(value, WebEncoders.Base64UrlEncode(WebEncoders.Base64UrlDecode(value)));
                Assert.True(values.Add(value));
            }
            Assert.Equal(clock.GetUtcNow().AddMinutes(5), start.ExpiresAt);
            Assert.DoesNotContain("Verifier", JsonSerializer.Serialize(start));
            Assert.DoesNotContain(start.Nonce, start.ToString()!);
            var consumed = Assert.IsType<ConsumedAdminLogin>(store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
            Assert.Equal(43, consumed.Verifier.Length);
            Assert.Equal(start.Nonce, consumed.Nonce);
            Assert.Equal(start.Challenge, WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(consumed.Verifier))));
            Assert.Equal(AdminLoginReturnTarget.Default, consumed.ReturnTarget);
            Assert.DoesNotContain(consumed.Verifier, consumed.ToString()!);
        }
        // Protocol state has no token/code/secret/identity field or raw state index.
        var fields = typeof(PendingAdminLoginStore).GetNestedType("Pending", System.Reflection.BindingFlags.NonPublic)!.GetProperties();
        Assert.Equivalent(new[] { "BindingHash", "Deadline", "Nonce", "Verifier", "ReturnTarget" }, fields.Select(p => p.Name).ToArray(), strict: true);
    }

    [Theory]
    [InlineData(null, "/#/books")]
    [InlineData("/books", "/#/books")]
    [InlineData("/#/books", "/#/books")]
    [InlineData("/books/Az09-_/words", "/#/books/Az09-_/words")]
    [InlineData("/#/books/Az09-_/words", "/#/books/Az09-_/words")]
    [InlineData("/vocabulary", "/#/vocabulary")]
    [InlineData("/phrases", "/#/phrases")]
    [InlineData("/import", "/#/import")]
    [InlineData("/import/phrase", "/#/import/phrase")]
    [InlineData("/import/batch", "/#/import/batch")]
    public void ReturnAllowlist_SavesOnlyNormalizedOriginalTarget(string? route, string expected)
    {
        var store = new PendingAdminLoginStore(new Clock());
        var start = Assert.IsType<PendingAdminLoginStart>(store.Create(route, TestContext.Current.CancellationToken));
        var consumed = Assert.IsType<ConsumedAdminLogin>(store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.Equal(expected, consumed.ReturnTarget);
        Assert.Equal(expected, AdminLoginReturnTarget.Normalize(route));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/forbidden")]
    [InlineData("//evil.test")]
    [InlineData("https://evil.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/books?x=1")]
    [InlineData("/books#extra")]
    [InlineData("/#/books#extra")]
    [InlineData("/books\\evil")]
    [InlineData("/books\r\nLocation:evil")]
    [InlineData("/books\0")]
    [InlineData("/%62ooks")]
    [InlineData("/%2562ooks")]
    [InlineData("/books/%2f/words")]
    [InlineData("/books/%/words")]
    [InlineData("/books/%zz/words")]
    [InlineData("/books/../words")]
    [InlineData("/books//words")]
    [InlineData("/books/a/b/words")]
    [InlineData("/books/词/words")]
    [InlineData("/Books")]
    [InlineData("/books/")]
    [InlineData("/#//evil.test")]
    [InlineData("/unknown")]
    [InlineData(" /books")]
    [InlineData("/books/a.b/words")]
    public void ReturnAttacks_AreRejectedInsteadOfSubstituted(string route)
    {
        var store = new PendingAdminLoginStore(new Clock());
        Assert.Null(store.Create(route, TestContext.Current.CancellationToken));
        Assert.Null(AdminLoginReturnTarget.Normalize(route));
    }

    [Fact]
    public void ReturnTarget_LengthAndBookIdLimits()
    {
        Assert.NotNull(AdminLoginReturnTarget.Normalize("/books/" + new string('A', 128) + "/words"));
        Assert.Null(AdminLoginReturnTarget.Normalize("/books/" + new string('A', 129) + "/words"));
        Assert.Null(AdminLoginReturnTarget.Normalize(new string('A', 257)));
    }

    [Fact]
    public void WrongBrowserOrState_DoesNotConsumeLegitimateTransaction_ThenReplayFails()
    {
        var store = new PendingAdminLoginStore(new Clock());
        var a = Start(store); var b = Start(store);
        Assert.Null(store.Consume(a.State, b.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.Null(store.Consume(b.State, a.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.NotNull(store.Consume(a.State, a.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.Null(store.Consume(a.State, a.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.NotNull(store.Consume(b.State, b.BrowserBinding, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/=")]
    public void MalformedAndAlternateBase64Selectors_DoNotExhaustValidTransaction(string? invalid)
    {
        var store = new PendingAdminLoginStore(new Clock());
        var start = Start(store);
        Assert.Null(store.Consume(invalid, start.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.Null(store.Consume(start.State, invalid, TestContext.Current.CancellationToken));
        Assert.NotNull(store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OversizedSelector_RejectedBeforeLookup()
    {
        var store = new PendingAdminLoginStore(new Clock());
        var start = Start(store);
        Assert.Null(store.Consume(new string('A', 10000), start.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.Null(store.Consume(start.State, new string('A', 10000), TestContext.Current.CancellationToken));
        Assert.NotNull(store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExactExpiryAndNewStoreRestart_RejectPending()
    {
        var clock = new Clock(); var store = new PendingAdminLoginStore(clock);
        var valid = Start(store); var expired = Start(store);
        clock.Advance(PendingAdminLoginStore.Lifetime - TimeSpan.FromTicks(1));
        Assert.NotNull(store.Consume(valid.State, valid.BrowserBinding, TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(store.Consume(expired.State, expired.BrowserBinding, TestContext.Current.CancellationToken));
        var after = Start(store);
        Assert.Null(new PendingAdminLoginStore(clock).Consume(after.State, after.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.NotNull(store.Consume(after.State, after.BrowserBinding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Capacity_IsBoundedNoLiveEviction_AndReclaimsExactExpiredEntries()
    {
        var clock = new Clock(); var store = new PendingAdminLoginStore(clock);
        var entries = Enumerable.Range(0, PendingAdminLoginStore.Capacity).Select(_ => Start(store)).ToArray();
        Assert.Null(store.Create(cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(store.Consume(entries[0].State, entries[0].BrowserBinding, TestContext.Current.CancellationToken));
        var later = Start(store);
        Assert.Null(store.Create(cancellationToken: TestContext.Current.CancellationToken));
        clock.Advance(PendingAdminLoginStore.Lifetime);
        var newEntry = Start(store);
        Assert.Null(store.Consume(later.State, later.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.Null(store.Consume(entries[^1].State, entries[^1].BrowserBinding, TestContext.Current.CancellationToken));
        Assert.NotNull(store.Consume(newEntry.State, newEntry.BrowserBinding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HundredConcurrentCallbacks_ExactlyOneSnapshot_NoResurrectionAfterFailure()
    {
        var store = new PendingAdminLoginStore(new Clock()); var start = Start(store);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(
            () => store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));
        Assert.Single(outcomes, result => result is not null);
        // All downstream exchange/sign-in/response failures leave the transaction spent.
        Assert.Null(store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentExpiryAndConsumption_AtMostOneWinner()
    {
        var clock = new Clock(); var store = new PendingAdminLoginStore(clock); var start = Start(store);
        clock.Advance(PendingAdminLoginStore.Lifetime - TimeSpan.FromTicks(1));
        var callbacks = Enumerable.Range(0, 100).Select(_ => Task.Run(() => store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)).ToArray();
        var expiry = Task.Run(() => clock.Advance(TimeSpan.FromTicks(1)), TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(callbacks); await expiry;
        Assert.InRange(results.Count(result => result is not null), 0, 1);
        Assert.Null(store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IndependentStartsAndCookieMetadata_OneBrowserDoesNotOverwriteAnotherTransaction()
    {
        var store = new PendingAdminLoginStore(new Clock()); var a = Start(store); var b = Start(store);
        Assert.NotEqual(a.BrowserBinding, b.BrowserBinding);
        Assert.NotEqual(a.CookieName, b.CookieName);
        Assert.Equal("__Host-Lexarbor.Login." + a.State, a.CookieName);
        foreach (var start in new[] { a, b })
        {
            var options = PendingAdminLoginCookie.ForStart(HostedLoginHttpTestTransport.Disabled, start);
            Assert.True(options.HttpOnly); Assert.True(options.Secure);
            Assert.Equal(SameSiteMode.Lax, options.SameSite); Assert.Equal("/", options.Path); Assert.Null(options.Domain);
            Assert.Equal(TimeSpan.FromMinutes(5), options.MaxAge); Assert.Equal(start.ExpiresAt, options.Expires);
        }
        var cleanup = PendingAdminLoginCookie.Attributes(HostedLoginHttpTestTransport.Disabled);
        Assert.Equal("/", cleanup.Path); Assert.True(cleanup.Secure); Assert.Null(cleanup.Domain);
        // Cancel first transaction by consumption, preserving the second and its cookie name.
        Assert.NotNull(store.Consume(a.State, a.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.Equal("__Host-Lexarbor.Login." + b.State, b.CookieName);
        Assert.NotNull(store.Consume(b.State, b.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => PendingAdminLoginCookie.Name(HostedLoginHttpTestTransport.Disabled, "malformed"));
    }

    [Fact]
    public void HttpTestTransport_LoginCookieUsesTestNameWithoutSecure()
    {
        var transport = HostedLoginHttpTestTransport.Create(
            new TestEnvironment("Testing"), "http://192.168.50.10:5008");
        var store = new PendingAdminLoginStore(new Clock(), transport); var start = Start(store);
        Assert.Equal("HttpTest-Lexarbor.Login." + start.State, start.CookieName);
        var options = PendingAdminLoginCookie.ForStart(transport, start);
        Assert.True(options.HttpOnly); Assert.False(options.Secure);
        Assert.Equal(SameSiteMode.Lax, options.SameSite); Assert.Equal("/", options.Path); Assert.Null(options.Domain);
        var cleanup = PendingAdminLoginCookie.Attributes(transport);
        Assert.False(cleanup.Secure); Assert.Equal("/", cleanup.Path); Assert.Null(cleanup.Domain);
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void CancellationBeforeMutation_PreservesTransaction_AndCanceledCreateDoesNotUseCapacity()
    {
        var store = new PendingAdminLoginStore(new Clock()); var start = Start(store);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Create(cancellationToken: cancel.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => store.Consume(start.State, start.BrowserBinding, cancel.Token));
        Assert.NotNull(store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.NotNull(store.Create(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RegisteredService_IsSingletonAcrossScopes_AndHostRestartLosesPending()
    {
        using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        using var a = factory.Services.CreateScope(); using var b = factory.Services.CreateScope();
        var store = a.ServiceProvider.GetRequiredService<PendingAdminLoginStore>();
        Assert.Same(store, b.ServiceProvider.GetRequiredService<PendingAdminLoginStore>());
        var start = Start(store);
        using var restarted = new VocabularyWebApplicationFactory(); using var otherClient = restarted.CreateClient();
        var other = restarted.Services.GetRequiredService<PendingAdminLoginStore>();
        Assert.Null(other.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
        Assert.NotNull(store.Consume(start.State, start.BrowserBinding, TestContext.Current.CancellationToken));
    }

    private static PendingAdminLoginStart Start(PendingAdminLoginStore store)
        => Assert.IsType<PendingAdminLoginStart>(store.Create(cancellationToken: TestContext.Current.CancellationToken));
    private sealed class Clock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
