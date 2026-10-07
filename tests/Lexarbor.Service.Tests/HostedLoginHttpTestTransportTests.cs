using System.Net;
using Lexarbor.Host.Authentication;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The Testing-only plain-HTTP transport policy: the SignaCore-compatible origin
/// syntax, the canonicalization rules, the cookie-name mapping and the exact
/// membership decision — all pure, before any host or endpoint is involved.
/// </summary>
public class HostedLoginHttpTestTransportTests
{
    private static HostedLoginHttpTestTransport Testing(string? configured) =>
        HostedLoginHttpTestTransport.Create(new TestEnvironment("Testing"), configured);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    [InlineData(" ; ; ")]
    public void MissingOrEmptyEntries_DisableTheTransport(string? configured)
    {
        var transport = Testing(configured);
        Assert.False(transport.Enabled);
        Assert.Equal(0, transport.AllowedOriginCount);
        Assert.Empty(HostedLoginHttpTestTransport.ParseOrigins(configured));
    }

    [Fact]
    public void ExactPrivateOrigins_AreCanonicalizedIntoKeys()
    {
        var origins = HostedLoginHttpTestTransport.ParseOrigins(
            "http://192.168.50.10:5008; http://10.0.0.1:80;http://[fd00::5]:5008;HTTP://172.31.255.254:65535");
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "http://192.168.50.10:5008",
                "http://10.0.0.1:80",
                "http://fd00::5:5008",
                "http://172.31.255.254:65535"
            },
            origins);
    }

    [Fact]
    public void EquivalentSpellings_CollapseToOneCanonicalOrigin()
    {
        // Scheme case, IPv6 case and zero compression, and whitespace all collapse;
        // an explicit :80 keeps its effective port in the canonical key.
        var origins = HostedLoginHttpTestTransport.ParseOrigins(
            "HTTP://[FD00:0:0:0:0:0:0:5]:5008;http://[fd00::5]:5008; http://10.0.0.1:80;http://10.0.0.1:80");
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "http://fd00::5:5008", "http://10.0.0.1:80" },
            origins);
    }

    [Theory]
    // Scheme / shape
    [InlineData("https://192.168.50.10:5008")]
    [InlineData("192.168.50.10:5008")]
    [InlineData("http://192.168.50.10")]
    [InlineData("http://192.168.50.10:5008/")]
    [InlineData("http://192.168.50.10:5008/path")]
    [InlineData("http://192.168.50.10:5008?q=1")]
    [InlineData("http://192.168.50.10:5008#f")]
    [InlineData("http://user@192.168.50.10:5008")]
    [InlineData("http://user:pass@192.168.50.10:5008")]
    [InlineData("http://192%2e168.50.10:5008")]
    [InlineData(@"http:\\192.168.50.10:5008")]
    // Host: DNS / localhost / wildcard / CIDR / aliases / mapped / non-private
    [InlineData("http://lexarbor.test:5008")]
    [InlineData("http://localhost:5008")]
    [InlineData("http://*.168.50.10:5008")]
    [InlineData("http://192.168.0.0/16:5008")]
    [InlineData("http://10.1:5008")]
    [InlineData("http://10.0.0:5008")]
    [InlineData("http://10.0.0.1.5:5008")]
    [InlineData("http://010.0.0.1:5008")]
    [InlineData("http://0x0a.0.0.1:5008")]
    [InlineData("http:://10.0.0.1:5008")]
    [InlineData("http://[::ffff:10.0.0.1]:5008")]
    [InlineData("http://[fe80::5]:5008")]
    [InlineData("http://127.0.0.1:5008")]
    [InlineData("http://[::1]:5008")]
    [InlineData("http://169.254.1.1:5008")]
    [InlineData("http://172.15.0.1:5008")]
    [InlineData("http://172.32.0.1:5008")]
    [InlineData("http://192.169.0.1:5008")]
    [InlineData("http://8.8.8.8:5008")]
    [InlineData("http://100.64.0.1:5008")]
    [InlineData("http://2001:db8::1:5008")]
    [InlineData("http://10.0.0.1:5008词")]
    // Port: missing, zero, out of range, non-decimal, leading zero
    [InlineData("http://10.0.0.1:0")]
    [InlineData("http://10.0.0.1:65536")]
    [InlineData("http://10.0.0.1:99999")]
    [InlineData("http://10.0.0.1:0x50")]
    [InlineData("http://10.0.0.1:+80")]
    [InlineData("http://10.0.0.1:080")]
    [InlineData("http://10.0.0.1:")]
    [InlineData("http://10.0.0.1:80:80")]
    [InlineData("http://[fd00::5]:5008:5008")]
    [InlineData("http://[fd00::5]")]
    public void InvalidOrigins_FailParsingWithAFixedDiagnostic(string entry)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => HostedLoginHttpTestTransport.ParseOrigins(entry));
        // The diagnostic explains the syntax and never echoes the configured value.
        Assert.Equal(HostedLoginHttpTestTransport.InvalidOriginsFailureMessage, exception.Message);
        Assert.DoesNotContain(entry, exception.Message, StringComparison.Ordinal);
        Assert.False(HostedLoginHttpTestTransport.ValidOrigins(entry));
    }

    [Theory]
    [InlineData("http://10.0.0.1:5008")]
    [InlineData("http://10.255.255.255:5008")]
    [InlineData("http://172.16.0.1:5008")]
    [InlineData("http://172.31.255.254:5008")]
    [InlineData("http://192.168.0.0:5008")]
    [InlineData("http://192.168.255.255:5008")]
    [InlineData("http://[fc00::1]:5008")]
    [InlineData("http://[fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff]:5008")]
    [InlineData("http://10.0.0.1:1")]
    [InlineData("http://10.0.0.1:65535")]
    [InlineData("http://10.0.0.1:80")]
    public void PrivateBoundaries_AreAccepted(string entry)
    {
        Assert.True(HostedLoginHttpTestTransport.ValidOrigins(entry));
        Assert.Single(HostedLoginHttpTestTransport.ParseOrigins(entry));
    }

    [Fact]
    public void MoreThanThirtyTwoOrigins_AreRefused()
    {
        var entries = string.Join(';', Enumerable.Range(1, 33).Select(i => $"http://10.0.{i / 256}.{i % 256}:5008"));
        Assert.Throws<InvalidOperationException>(() => HostedLoginHttpTestTransport.ParseOrigins(entries));
        var thirtyTwo = string.Join(';', Enumerable.Range(1, 32).Select(i => $"http://10.0.{i / 256}.{i % 256}:5008"));
        Assert.Equal(32, HostedLoginHttpTestTransport.ParseOrigins(thirtyTwo).Count);
    }

    [Fact]
    public void OversizedConfiguration_IsRefused()
    {
        var entries = string.Join(';', Enumerable.Range(1, 32).Select(i => $"http://10.0.{i / 256}.{i % 256}:{5000 + i % 1000}" + new string('0', 300)));
        Assert.True(entries.Length > HostedLoginHttpTestTransport.MaximumLength);
        Assert.Throws<InvalidOperationException>(() => HostedLoginHttpTestTransport.ParseOrigins(entries));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void NonTestingEnvironmentWithOrigins_FailsFast(string environment)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => HostedLoginHttpTestTransport.Create(new TestEnvironment(environment), "http://192.168.50.10:5008"));
        Assert.Equal(HostedLoginHttpTestTransport.NonTestingFailureMessage, exception.Message);
        // The empty configuration stays valid in every environment.
        Assert.False(HostedLoginHttpTestTransport.Create(new TestEnvironment(environment), null).Enabled);
    }

    [Fact]
    public void Create_EnablesOnlyWithTestingEnvironmentAndNonEmptyList()
    {
        Assert.True(Testing("http://192.168.50.10:5008").Enabled);
        Assert.False(Testing(null).Enabled);
    }

    [Fact]
    public void Allows_MatchesCanonicalOriginExactly()
    {
        var transport = Testing("http://192.168.50.10:5008;http://[fd00::5]:8080");
        Assert.True(transport.Allows(new Uri("http://192.168.50.10:5008/admin/auth/callback?registered=1")));
        Assert.True(transport.Allows(new Uri("http://[FD00::5]:8080/admin/auth/logout/return")));
        // Different host, port or scheme — including the same host over HTTPS —
        // never matches, and neither does an omitted port (effective 80).
        Assert.False(transport.Allows(new Uri("http://192.168.50.10:5009/admin/auth/callback")));
        Assert.False(transport.Allows(new Uri("http://192.168.50.11:5008/admin/auth/callback")));
        Assert.False(transport.Allows(new Uri("https://192.168.50.10:5008/admin/auth/callback")));
        Assert.False(transport.Allows(new Uri("http://192.168.50.10/admin/auth/callback")));
        Assert.False(transport.Allows(new Uri("http://8.8.8.8:5008/admin/auth/callback")));
    }

    [Fact]
    public void CookieName_ReplacesOnlyTheHostPrefixWhenEnabled()
    {
        var transport = Testing("http://192.168.50.10:5008");
        Assert.Equal("HttpTest-Lexarbor.AdminSession", transport.CookieName("__Host-Lexarbor.AdminSession"));
        Assert.Equal("HttpTest-Lexarbor.Login.state", transport.CookieName("__Host-Lexarbor.Login.state"));
        Assert.Equal("HttpTest-Lexarbor.Logout.state", transport.CookieName("__Host-Lexarbor.Logout.state"));
        Assert.Equal("lexarborAdmin", transport.CookieName("lexarborAdmin"));
        // Disabled — the default every HTTPS deployment runs under — never rewrites.
        Assert.False(HostedLoginHttpTestTransport.Disabled.Enabled);
        Assert.Equal("__Host-Lexarbor.AdminSession", HostedLoginHttpTestTransport.Disabled.CookieName("__Host-Lexarbor.AdminSession"));
        Assert.Equal("lexarborAdmin", HostedLoginHttpTestTransport.Disabled.CookieName("lexarborAdmin"));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
