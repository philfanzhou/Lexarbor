using System.Text.Json;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;

namespace Lexarbor.Service.Dtos;

public sealed record VocabularyCleanupRequest(string Action, string? WordId, string? MeaningId,
    IReadOnlyList<string>? WordIds, string? ConfirmedBookName)
{
    public VocabularyCleanupSelection ToSelection() => new(Action, WordId, MeaningId, WordIds, ConfirmedBookName);

    public static VocabularyCleanupRequest Parse(JsonElement body, bool preview)
    {
        if (body.ValueKind != JsonValueKind.Object) throw Invalid();
        var action = RequiredString(body, "action");
        string[] fields = action switch
        {
            "removeMeaning" => ["action", "wordId", "meaningId"],
            "removeWords" => ["action", "wordIds"],
            "clear" => ["action"],
            "delete" => ["action", "confirmedBookName"],
            _ => throw Invalid()
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Invalid();
        string? word = null, meaning = null, name = null;
        List<string>? words = null;
        if (action == "removeMeaning")
        {
            word = RequiredString(body, "wordId"); meaning = RequiredString(body, "meaningId");
        }
        if (action == "removeWords")
        {
            if (!body.TryGetProperty("wordIds", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 100) throw Invalid();
            words = [];
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) throw Invalid();
                words.Add(id.GetString()!);
            }
        }
        if (action == "delete" && (!preview || seen.Contains("confirmedBookName")))
        {
            if (!body.TryGetProperty("confirmedBookName", out var confirmation) || confirmation.ValueKind != JsonValueKind.String) throw Invalid();
            name = confirmation.GetString();
        }
        return new(action, word, meaning, words, name);
    }

    private static string RequiredString(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw Invalid();
        return value.GetString()!;
    }
    private static DomainValidationException Invalid() => new("The cleanup request is invalid.");
}
