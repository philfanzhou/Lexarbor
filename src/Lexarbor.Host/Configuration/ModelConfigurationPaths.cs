using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Lexarbor.Host.Configuration;

/// <summary>Resolve whole model connections. Never combine credentials and endpoints from different sections.</summary>
internal sealed record ModelConfigurationPaths(string Reasoning, string Speech)
{
    internal const string ReasoningKey = "Models:Reasoning";
    internal const string SpeechKey = "Models:Speech";
    internal const string OldText = "Enrichment:Text";
    internal const string OldSpeech = "Enrichment:Tts";
    internal const string OldReview = "MeaningSubmissions:Ai";

    internal static ModelConfigurationPaths Resolve(IConfiguration configuration, JsonObject document)
    {
        var file = new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document))).Build();
        using var lifetime = file as IDisposable;
        string ResolveGroup(string logical, string canonical, params string[] legacy)
        {
            var fields = EnrichmentConfigurationFields.All.Where(f => f.Key.StartsWith(logical + ":", StringComparison.Ordinal))
                .Select(f => (Suffix: f.Key[logical.Length..], f.Default)).ToArray();
            var sections = legacy.Prepend(canonical).ToArray();
            if (configuration is IConfigurationRoot root)
            {
                foreach (var provider in root.Providers.Reverse().Where(p =>
                    p is EnvironmentVariablesConfigurationProvider or CommandLineConfigurationProvider))
                {
                    var overridden = sections.FirstOrDefault(section => fields.Any(f => provider.TryGet(section + f.Suffix, out _)));
                    if (overridden is not null) return overridden;
                }
            }
            // An explicitly cleared model stays cleared, even if an unused legacy connection is still on disk.
            if (file.GetSection(canonical).Exists()) return canonical;
            bool HasValue(string section, string suffix) => !string.IsNullOrWhiteSpace(configuration[section + suffix]);
            if (logical == OldText && !HasValue(canonical, ":Endpoint") && !HasValue(canonical, ":Model") && !HasValue(canonical, ":ApiKey"))
            {
                if (file["EnrichmentConfigurationVersions:ReasoningSection"] == OldText) return OldText;
                var complete = legacy.FirstOrDefault(section => HasValue(section, ":Endpoint") && HasValue(section, ":Model"));
                if (complete is not null) return complete;
            }
            foreach (var section in sections)
            {
                if (HasValue(section, ":Endpoint") || HasValue(section, ":ApiKey") ||
                    logical == OldText && HasValue(section, ":Model"))
                    return section;
            }
            // With no active connection, retain partial operator settings such as an accent or timeout.
            foreach (var section in sections)
                if (fields.Any(f => configuration[section + f.Suffix] is { Length: > 0 } value &&
                    !string.Equals(value, Convert.ToString(f.Default, System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.OrdinalIgnoreCase))) return section;
            return canonical;
        }

        var reasoning = ResolveGroup(OldText, ReasoningKey, OldText, OldReview);
        var speech = ResolveGroup(OldSpeech, SpeechKey, OldSpeech);
        return new(reasoning, speech);
    }

    // Stable internal field identifiers also preserve pre-upgrade task-version digests.
    internal string PhysicalKey(string field)
    {
        if (field.StartsWith(OldText + ":", StringComparison.OrdinalIgnoreCase)) return Reasoning + field[OldText.Length..];
        if (field.StartsWith(OldSpeech + ":", StringComparison.OrdinalIgnoreCase)) return Speech + field[OldSpeech.Length..];
        return field;
    }
}
