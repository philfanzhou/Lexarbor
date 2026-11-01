using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed class AudioAdminQueryEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false, "admin")]
    [InlineData(true, false, "admin")]
    [InlineData(false, true, "admin")]
    [InlineData(true, true, "maintainer")]
    public async Task Reads_UseExistingIdentityAndExposeOnlySafeContracts(bool cookie, bool enabled, string role)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["AdminAuthentication:RequiredRole"] = role,
            ["Audio:Enabled"] = enabled.ToString(),
            ["Audio:InternalEndpoint"] = "http://127.0.0.1:1",
            ["Audio:PublicEndpoint"] = "https://audio.example.test/",
            ["Audio:AccessKey"] = "synthetic-access",
            ["Audio:SecretKey"] = "synthetic-secret"
        };
        await using var factory = new VocabularyWebApplicationFactory("Testing", true, extraConfiguration: configuration);
        // Any accidental object-store dependency or invocation fails the read.
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAudioObjectStore>();
            services.AddSingleton<IAudioObjectStore>(_ => throw new InvalidOperationException("Reads must not resolve object storage."));
        }));
        await SeedAsync(host.Services);
        using var client = host.CreateClient();
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie(role));
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(role));
        using var metadata = await GetAsync(client, "/admin/vocabulary/w/audio?meaningId=first");
        Assert.Equal(enabled, metadata.RootElement.GetProperty("enabled").GetBoolean());
        var items = metadata.RootElement.GetProperty("items");
        Assert.Equal(new[] { "en-GB", "en-US" }, items.EnumerateArray().Select(item => item.GetProperty("locale").GetString()));
        var gb = items[0];
        Assert.Equal("ready", gb.GetProperty("status").GetString());
        Assert.Equal("default", gb.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, gb.GetProperty("ownBinding").ValueKind);
        Assert.Equal("default-gb", gb.GetProperty("effectiveBinding").GetProperty("assetId").GetString());
        Assert.NotEqual(gb.GetProperty("sourceRevision").GetString(), gb.GetProperty("effectiveBinding").GetProperty("sourceRevision").GetString());
        Assert.Equal("first definition", gb.GetProperty("input").GetProperty("meaning").GetString());
        Assert.Equal("missing", items[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("source").ValueKind);
        using var existing = await GetAsync(client, "/admin/vocabulary/w/audio?meaningId=override");
        var overridden = existing.RootElement.GetProperty("items")[0];
        Assert.Equal("stale", overridden.GetProperty("status").GetString());
        Assert.Equal("meaning", overridden.GetProperty("source").GetString());
        Assert.Equal("override-gb", overridden.GetProperty("ownBinding").GetProperty("assetId").GetString());
        Assert.Equal("override-gb", overridden.GetProperty("effectiveBinding").GetProperty("assetId").GetString());
        using var unassigned = await GetAsync(client, "/admin/vocabulary/orphan/audio");
        Assert.All(unassigned.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal("missing", item.GetProperty("status").GetString()));
        using var listResponse = await client.GetAsync("/admin/audio-targets?bookId=disabled", Token);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(listResponse.Headers.Contains("X-Content-Type-Options"));
        var json = await listResponse.Content.ReadAsStringAsync(Token);
        foreach (var forbidden in new[] { "bucket", "objectKey", "synthetic", "endpoint", "url", "audio/internal" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        using var list = JsonDocument.Parse(json);
        Assert.Equal(3, list.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(2, await db.AudioAssets.CountAsync(Token));
        Assert.Equal(2, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Empty(await db.AudioUploadOperations.ToListAsync(Token));
    }

    [Fact]
    public async Task LegacyOverrideWithoutSpecificPhonetic_RemainsManageableButSelectsDefault()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await db.VocabularyMeanings.Where(row => row.Id == "override")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, (string?)null), Token);
        }
        using var client = Admin(factory);
        using var metadata = await GetAsync(client, "/admin/vocabulary/w/audio?meaningId=override");
        var item = metadata.RootElement.GetProperty("items")[0];
        Assert.Equal("ready", item.GetProperty("status").GetString());
        Assert.Equal("default", item.GetProperty("source").GetString());
        Assert.Equal("override-gb", item.GetProperty("ownBinding").GetProperty("assetId").GetString());
        Assert.Equal("default-gb", item.GetProperty("effectiveBinding").GetProperty("assetId").GetString());
        using var ready = await GetAsync(client, "/admin/audio-targets?status=ready");
        Assert.Equal(2, ready.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_OnlyDefaultsAndExistingLocales_AreSortedFilteredAndPaged()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory.Services);
        using var client = Admin(factory);
        using var all = await GetAsync(client, "/admin/audio-targets");
        Assert.Equal(5, all.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, all.RootElement.GetProperty("totalPage").GetInt32());
        var items = all.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { "orphan:en-GB", "orphan:en-US", "w:en-GB", "w:en-US", "override:en-GB" },
            items.Select(item => (item.GetProperty("meaningId").GetString() ?? item.GetProperty("wordId").GetString()) + ":" + item.GetProperty("locale").GetString()));
        using var page = await GetAsync(client, "/admin/audio-targets?page=3&size=2");
        Assert.Equal(5, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, page.RootElement.GetProperty("totalPage").GetInt32());
        Assert.Equal("override", page.RootElement.GetProperty("items")[0].GetProperty("meaningId").GetString());
        using var past = await GetAsync(client, "/admin/audio-targets?page=4&size=2");
        Assert.Empty(past.RootElement.GetProperty("items").EnumerateArray());
        foreach (var (query, expected) in new[] { ("locale=en-US", 2), ("status=missing", 3), ("status=ready", 1), ("status=stale", 1), ("bookId=empty", 0), ("bookId=disabled&status=missing&locale=en-US", 1) })
        {
            using var filtered = await GetAsync(client, "/admin/audio-targets?" + query);
            Assert.Equal(expected, filtered.RootElement.GetProperty("totalCount").GetInt32());
        }
    }

    [Theory]
    [InlineData("/admin/audio-targets?locale=fr-FR", 400)]
    [InlineData("/admin/audio-targets?locale=", 400)]
    [InlineData("/admin/audio-targets?status=Ready", 400)]
    [InlineData("/admin/audio-targets?status=", 400)]
    [InlineData("/admin/audio-targets?page=0", 400)]
    [InlineData("/admin/audio-targets?size=0", 400)]
    [InlineData("/admin/audio-targets?page=-1", 400)]
    [InlineData("/admin/audio-targets?size=101", 400)]
    [InlineData("/admin/audio-targets?page=2147483647&size=100", 400)]
    [InlineData("/admin/audio-targets?page=2147483648", 400)]
    [InlineData("/admin/audio-targets?bookId=", 400)]
    [InlineData("/admin/audio-targets?bookId=absent", 404)]
    [InlineData("/admin/vocabulary/absent/audio", 404)]
    [InlineData("/admin/vocabulary/orphan/audio?meaningId=first", 404)]
    [InlineData("/admin/vocabulary/w/audio?meaningId=absent", 404)]
    [InlineData("/admin/vocabulary/w/audio?meaningId=", 400)]
    public async Task InvalidParametersAndMissingOwnership_AreRejected(string path, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory.Services);
        using var client = Admin(factory);
        using var response = await client.GetAsync(path, Token);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("/admin/audio-targets")]
    [InlineData("/admin/vocabulary/absent/audio")]
    public async Task Authentication_RejectsAnonymousAndNonAdmin_AndBearerKeepsPrecedence(string path)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        using var anonymous = await client.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("student"));
        using var denied = await client.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie("admin"));
        using var mixed = await client.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.Forbidden, mixed.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        using var invalid = await client.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task Paging_DefaultSizeAndMaximumAndBatchBoundary_DoNotLoseTargets()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.AddRange(Enumerable.Range(0, 205).Select(i => new VocabularyEntity { Id = $"word-{i:D3}", Word = $"word {i}" }));
            await db.SaveChangesAsync(Token);
        }
        using var client = Admin(factory);
        using var first = await GetAsync(client, "/admin/audio-targets");
        Assert.Equal(20, first.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(410, first.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(21, first.RootElement.GetProperty("totalPage").GetInt32());
        using var maximum = await GetAsync(client, "/admin/audio-targets?size=100");
        Assert.Equal(100, maximum.RootElement.GetProperty("items").GetArrayLength());
        using var last = await GetAsync(client, "/admin/audio-targets?size=100&page=5");
        Assert.Equal(10, last.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("word-204", last.RootElement.GetProperty("items")[9].GetProperty("wordId").GetString());
    }

    [Theory]
    [InlineData("uk", true, false, false, false)]
    [InlineData("us", false, true, false, false)]
    [InlineData("word", true, true, true, true)]
    [InlineData("pos", false, false, true, true)]
    [InlineData("meaning", false, false, true, true)]
    [InlineData("example", false, false, false, false)]
    [InlineData("import", true, false, false, false)]
    public async Task ExistingEditsAndImport_ChangeOnlyTheAffectedHttpStates(string change,
        bool defaultGb, bool defaultUs, bool overrideGb, bool overrideUs)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            (await db.VocabularyBooks.SingleAsync(book => book.Id == "disabled", Token)).Status = true;
            foreach (var locale in Enum.GetValues<AudioLocale>())
                foreach (var isMeaning in new[] { false, true })
                {
                    var revision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("w", "Apple", "uk", "us"),
                        isMeaning ? new("override", "v.", "override definition") : null, locale));
                    var own = await db.VocabularyAudioBindings.SingleOrDefaultAsync(binding => binding.WordId == "w"
                        && binding.MeaningId == (isMeaning ? "override" : null) && binding.Locale == locale, Token);
                    if (own != null) own.SourceRevision = revision;
                    else
                    {
                        var id = $"{locale}-{isMeaning}";
                        db.AudioAssets.Add(new AudioAssetEntity { Id = id, Bucket = "test-audio", ObjectKey = "audio/" + id + ".mp3", Sha256 = new string('a', 64), ByteLength = 128, CreatedAtUnixMs = 1, FirstPublishedAtUnixMs = 2 });
                        db.VocabularyAudioBindings.Add(new VocabularyAudioBindingEntity { Id = id, WordId = "w", MeaningId = isMeaning ? "override" : null, Locale = locale, AssetId = id, SourceRevision = revision });
                    }
                }
            await db.SaveChangesAsync(Token);
            var words = scope.ServiceProvider.GetRequiredService<VocabularyWordEditService>();
            var meanings = scope.ServiceProvider.GetRequiredService<VocabularyMeaningEditService>();
            switch (change)
            {
                case "uk": await words.ReplaceAsync("w", "Apple", "new-uk", "us", Token); break;
                case "us": await words.ReplaceAsync("w", "Apple", "uk", "new-us", Token); break;
                case "word": await words.ReplaceAsync("w", "APPLE", "uk", "us", Token); break;
                case "pos": await meanings.ReplaceAsync("disabled", "w", "override", "n.", "override definition", null, Token); break;
                case "meaning": await meanings.ReplaceAsync("disabled", "w", "override", "v.", "updated definition", null, Token); break;
                case "example": await meanings.ReplaceAsync("disabled", "w", "override", "v.", "override definition", "updated example", Token); break;
                case "import":
                    var imported = await scope.ServiceProvider.GetRequiredService<VocabularyDomainService>().ImportBatchAsync("disabled",
                        [(new() { Word = "Apple", PhoneticUk = "import-uk" }, new() { PartOfSpeech = "v.", Meaning = "override definition" }, null, null, null)]);
                    Assert.Equal(1, imported.Reused);
                    break;
            }
        }
        using var client = Admin(factory);
        using var defaults = await GetAsync(client, "/admin/vocabulary/w/audio");
        using var overrides = await GetAsync(client, "/admin/vocabulary/w/audio?meaningId=override");
        foreach (var (document, gb, us) in new[] { (defaults, defaultGb, defaultUs), (overrides, overrideGb, overrideUs) })
        {
            Assert.Equal(gb ? "stale" : "ready", document.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
            Assert.Equal(us ? "stale" : "ready", document.RootElement.GetProperty("items")[1].GetProperty("status").GetString());
        }
        using var filtered = await GetAsync(client, "/admin/audio-targets?status=stale");
        Assert.Equal(new[] { defaultGb, defaultUs, overrideGb, overrideUs }.Count(stale => stale), filtered.RootElement.GetProperty("totalCount").GetInt32());
    }

    private static HttpClient Admin(VocabularyWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        return client;
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return JsonDocument.Parse(body.RootElement.GetProperty("data").GetRawText());
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "disabled", BookName = "Disabled", Status = false },
            new VocabularyBookEntity { Id = "empty", BookName = "Empty", Status = true });
        db.Vocabularies.AddRange(new VocabularyEntity { Id = "w", Word = "Apple", PhoneticUk = "uk", PhoneticUs = "us" },
            new VocabularyEntity { Id = "orphan", Word = "orphan" });
        db.VocabularyMeanings.AddRange(new VocabularyMeaningEntity { Id = "first", VocabularyId = "w", BookId = "disabled", PartOfSpeech = "n.", Meaning = "first definition" },
            new VocabularyMeaningEntity { Id = "override", VocabularyId = "w", BookId = "disabled", PartOfSpeech = "v.", Meaning = "override definition", PhoneticUk = "uk", PhoneticUs = "us" });
        foreach (var (id, meaning, revision) in new[] { ("default-gb", (string?)null, AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("w", "Apple", "uk", "us"), null, AudioLocale.EnGb))), ("override-gb", "override", new string('b', 64)) })
        {
            db.AudioAssets.Add(new AudioAssetEntity { Id = id, Bucket = "test-audio", ObjectKey = "audio/internal/" + id + ".mp3", Sha256 = new string('a', 64), ByteLength = 128, CreatedAtUnixMs = 1, FirstPublishedAtUnixMs = 2 });
            db.VocabularyAudioBindings.Add(new VocabularyAudioBindingEntity { Id = id, WordId = "w", MeaningId = meaning, Locale = AudioLocale.EnGb, AssetId = id, SourceRevision = revision });
        }
        await db.SaveChangesAsync(Token);
    }
}
