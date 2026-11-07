using System.Net;
using System.Text.Json.Nodes;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentSettingsTests
{
    [Fact]
    public async Task TextConcurrency_DefaultsToOne_PersistsIndependently_AndValidatesBounds()
    {
        using (var factory = Factory())
        using (var client = Admin(factory))
        {
            var state = await Read(client);
            Assert.Equal(1, state["enrichment"]!["textConcurrency"]!.GetValue<int>());
            Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"enrichment":{"textConcurrency":2}}""")).StatusCode);
            Assert.Equal(2, Snapshot(factory).Enrichment.TextConcurrency);
            Assert.Equal(1, Snapshot(factory).Enrichment.Concurrency);
            var saved = JsonNode.Parse(File.ReadAllText(FilePath))!;
            Assert.Equal(2, saved["Enrichment"]!["TextConcurrency"]!.GetValue<int>());
            foreach (var invalid in new[] { "0", "3", "1.5", "null", "true" })
            {
                state = await Read(client);
                Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, Revision(state), "{\"enrichment\":{\"textConcurrency\":" + invalid + "}}")).StatusCode);
                Assert.Equal(2, Snapshot(factory).Enrichment.TextConcurrency);
            }
        }
        using var restarted = Factory(); using var reader = Admin(restarted);
        Assert.Equal(2, (await Read(reader))["enrichment"]!["textConcurrency"]!.GetValue<int>());
    }
}
