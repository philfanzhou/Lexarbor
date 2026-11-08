using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host.Configuration;

namespace Lexarbor.Host.Integration;

// Provider tuning belongs to the request, including the first attempt. Never
// send model-specific switches to an unrelated compatible endpoint or reviews.
internal static class EnrichmentTextRequest
{
    internal static byte[] Body(EnrichmentClientSettings settings, string instructions, string proposal,
        int maximumTokens, bool enrichment = true)
    {
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["temperature"] = 0,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = instructions },
                new JsonObject { ["role"] = "user", ["content"] = proposal })
        };
        if (enrichment && settings.Model == "MiniMax-M3.1-Flash" && SpeechProviderCapabilities.OfficialMiniMax(settings.Endpoint))
        {
            // Official Chat Completions counts reasoning in this budget and
            // otherwise defaults this model to maximum reasoning effort.
            body["reasoning_effort"] = "low";
            body["max_completion_tokens"] = Math.Clamp(maximumTokens, 4096, 8192);
        }
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }
}
