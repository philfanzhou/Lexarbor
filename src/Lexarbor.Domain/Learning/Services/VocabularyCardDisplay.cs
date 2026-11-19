using System.Text.Json;
using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

/// <summary>
/// Word and meaning text for a memory card, read from its latest issued question snapshot.
/// Cards store only opaque keys; a missing or damaged snapshot falls back to those keys.
/// </summary>
internal static class VocabularyCardDisplay
{
    public static (string Word, string Meaning, bool Incomplete) Read(string wordKey, string meaningKey,
        VocabularyErrorProneDisplay? display)
    {
        if (display is null) return (wordKey, meaningKey, true);
        var direction = (VocabQuestionDirection)display.Direction;
        if (direction.IsSpelling())
            return !string.IsNullOrWhiteSpace(display.AnswerWord) && !string.IsNullOrWhiteSpace(display.Prompt)
                ? (display.AnswerWord, display.Prompt, false) : (wordKey, meaningKey, true);
        if (direction == VocabQuestionDirection.RelationChoice)
            return string.IsNullOrWhiteSpace(display.Prompt) ? (wordKey, meaningKey, true) : (display.Prompt, meaningKey, false);
        if (!Enum.IsDefined(direction) || string.IsNullOrWhiteSpace(display.Prompt)
            || display.CorrectOptionId is null || display.OptionsSnapshot is null)
            return (wordKey, meaningKey, true);
        try
        {
            using var document = JsonDocument.Parse(display.OptionsSnapshot);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return (wordKey, meaningKey, true);
            string? answer = null;
            foreach (var option in document.RootElement.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("optionId", out var id) ||
                    id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var value) || value != display.CorrectOptionId) continue;
                if (answer is not null || !option.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    return (wordKey, meaningKey, true);
                answer = text.GetString();
            }
            if (string.IsNullOrWhiteSpace(answer)) return (wordKey, meaningKey, true);
            return direction switch
            {
                VocabQuestionDirection.EnglishToChinese => (display.Prompt, answer, false),
                VocabQuestionDirection.FormChoice => (answer, meaningKey, false),
                _ => (answer, display.Prompt, false),
            };
        }
        catch (JsonException) { return (wordKey, meaningKey, true); }
    }
}
