using System.Text;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Learning.Service;

/// <summary>
/// Strict decoding of the Lexarbor learning-question route (POST /api/vocabulary/learning-question).
/// Only 404 QUESTION_TARGET_NOT_FOUND and 422 QUESTION_SOURCE_UNAVAILABLE/QUESTION_UNAVAILABLE with
/// success=false mean "unavailable" (null). Every other status or code, an unreadable envelope and any
/// response that breaks the provider contract throws VocabCatalogUnavailableException. Response bodies
/// never reach exceptions or logs. Validation checks structure only; it does not repeat Lexarbor's
/// letter-hiding eligibility rules.
/// </summary>
internal static class CatalogLearningQuestionReader
{
    internal const string NewMode = "new";
    internal const string ReviewMode = "review";

    internal static string ModeName(VocabGenerationMode mode) => mode == VocabGenerationMode.Review ? ReviewMode : NewMode;

    internal static async Task<VocabGeneratedQuestion?> ReadAsync(
        HttpResponseMessage response, CatalogLearningQuestionRequest request, ILogger? logger, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        using var document = await ParseAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document?.RootElement;
        if (status == 200 && root is { ValueKind: JsonValueKind.Object } envelope
            && envelope.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
            && envelope.TryGetProperty("data", out var data))
        {
            var question = Decode(data, request);
            if (question is not null) return question;
            logger?.LogWarning("Vocabulary learning question response did not match the provider contract.");
            throw new VocabCatalogUnavailableException();
        }

        if (status is 404 or 422 && root is { ValueKind: JsonValueKind.Object } failure
            && failure.TryGetProperty("success", out var failed) && failed.ValueKind == JsonValueKind.False
            && failure.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
        {
            var code = codeElement.GetString();
            if ((status == 404 && code == "QUESTION_TARGET_NOT_FOUND")
                || (status == 422 && code is "QUESTION_SOURCE_UNAVAILABLE" or "QUESTION_UNAVAILABLE"))
            {
                return null;
            }
        }

        logger?.LogWarning("Vocabulary learning question endpoint answered {Status} without an accepted result.", status);
        throw new VocabCatalogUnavailableException();
    }

    private static async Task<JsonDocument?> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client timeout fired while the body was read.
            return null;
        }
    }

    /// <summary>Returns null when the data does not match exactly one current union shape for the mode.</summary>
    private static VocabGeneratedQuestion? Decode(JsonElement data, CatalogLearningQuestionRequest request)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !TryString(data, "type", out var type)
            || !TryString(data, "prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
            return null;

        VocabQuestionDirection direction;
        switch (type)
        {
            case "EnglishToChinese": direction = VocabQuestionDirection.EnglishToChinese; break;
            case "ChineseToEnglish": direction = VocabQuestionDirection.ChineseToEnglish; break;
            case "FillLetters": direction = VocabQuestionDirection.FillLetters; break;
            case "FormChoice": direction = VocabQuestionDirection.FormChoice; break;
            case "RelationChoice": direction = VocabQuestionDirection.RelationChoice; break;
            case "FormFill": direction = VocabQuestionDirection.FormFill; break;
            case "PartOfSpeechChoice": direction = VocabQuestionDirection.PartOfSpeechChoice; break;
            default: return null;
        }
        if (request.Mode == NewMode && direction != VocabQuestionDirection.EnglishToChinese) return null;
        if (request.Types is { } allowed && !allowed.Contains(type)) return null;
        if (direction.IsLinked())
            return request.Mode == ReviewMode && request.Relation is { } relation
                ? DecodeLinked(data, direction, prompt, relation.Type) : null;

        if (direction == VocabQuestionDirection.PartOfSpeechChoice)
        {
            if (!TryString(data, "anchorWord", out var anchorWord) || string.IsNullOrWhiteSpace(anchorWord)
                || !TryString(data, "partOfSpeech", out var partOfSpeech) || !IsPartOfSpeech(partOfSpeech)
                || data.TryGetProperty("answerWord", out _) || data.TryGetProperty("pattern", out _)
                || data.TryGetProperty("relationType", out _) || !data.TryGetProperty("options", out var choices))
                return null;
            var question = DecodeChoice(direction, prompt, choices);
            if (question is null
                || question.Options.Any(option => string.Equals(option.Text.Trim(), anchorWord.Trim(), StringComparison.OrdinalIgnoreCase))
                || question.Options.Select(option => option.Text.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
                return null;
            return question with { AnchorWord = anchorWord, PartOfSpeech = partOfSpeech };
        }

        var hasOptions = data.TryGetProperty("options", out var options);
        var hasPattern = data.TryGetProperty("pattern", out var pattern);
        var hasAnswer = data.TryGetProperty("answerWord", out var answer);
        return direction == VocabQuestionDirection.FillLetters
            ? hasOptions || !hasPattern || !hasAnswer ? null : DecodeFill(direction, prompt, pattern, answer)
            : hasPattern || hasAnswer || !hasOptions ? null : DecodeChoice(direction, prompt, options);
    }

    private static VocabGeneratedQuestion? DecodeLinked(JsonElement data, VocabQuestionDirection direction, string prompt, string requestedType)
    {
        var hasOptions = data.TryGetProperty("options", out var options);
        var hasPattern = data.TryGetProperty("pattern", out var pattern);
        var hasAnswer = data.TryGetProperty("answerWord", out var answer);
        if (data.TryGetProperty("partOfSpeech", out _)) return null;

        if (direction == VocabQuestionDirection.RelationChoice)
        {
            if (!hasOptions || hasPattern || hasAnswer || data.TryGetProperty("relationType", out _)
                || !TryString(data, "anchorWord", out var anchor) || string.IsNullOrWhiteSpace(anchor))
                return null;
            var question = DecodeChoice(direction, prompt, options);
            if (question is null || question.Options.Single(option => option.IsCorrect).Text != requestedType
                || !RelationOptionsMatch(question.Options, requestedType))
                return null;
            return question with { AnchorWord = anchor };
        }

        if (!TryString(data, "relationType", out var relationType) || relationType != requestedType
            || !VocabularyFamilyRelation.ValidType(relationType)) return null;
        if (direction == VocabQuestionDirection.FormChoice)
        {
            if (!hasOptions || hasPattern || hasAnswer || data.TryGetProperty("anchorWord", out _)) return null;
            var question = DecodeChoice(direction, prompt, options);
            return question?.Options.Select(option => option.Text.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4
                ? question with { RelationType = relationType } : null;
        }

        if (hasOptions || !hasPattern || !hasAnswer
            || !TryString(data, "anchorWord", out var source) || string.IsNullOrWhiteSpace(source))
            return null;
        var fill = DecodeFill(direction, prompt, pattern, answer);
        return fill is null ? null : fill with { AnchorWord = source, RelationType = relationType };
    }

    private static bool RelationOptionsMatch(IReadOnlyList<VocabGeneratedOption> options, string requestedType) => requestedType switch
    {
        "derived_noun" or "derived_verb" or "derived_adjective" or "derived_adverb" =>
            options.All(option => option.Text is "derived_noun" or "derived_verb" or "derived_adjective" or "derived_adverb"),
        "past_tense" or "past_participle" or "ing_form" or "third_person_singular" =>
            options.All(option => option.Text is "past_tense" or "past_participle" or "ing_form" or "third_person_singular"),
        _ => false,
    };

    private static VocabGeneratedQuestion? DecodeChoice(VocabQuestionDirection direction, string prompt, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array || options.GetArrayLength() != 4) return null;
        var decoded = new List<VocabGeneratedOption>(4);
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object
                || !TryString(option, "text", out var text) || string.IsNullOrWhiteSpace(text)
                || !option.TryGetProperty("isCorrect", out var correct)
                || correct.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            decoded.Add(new VocabGeneratedOption(text, correct.ValueKind == JsonValueKind.True));
        }
        if (decoded.Count(option => option.IsCorrect) != 1
            || decoded.Select(option => option.Text).Distinct(StringComparer.Ordinal).Count() != decoded.Count)
            return null;
        return new VocabGeneratedQuestion(direction, prompt, decoded);
    }

    private static VocabGeneratedQuestion? DecodeFill(VocabQuestionDirection direction, string prompt, JsonElement pattern, JsonElement answerElement)
    {
        if (answerElement.ValueKind != JsonValueKind.String || pattern.ValueKind != JsonValueKind.Array) return null;
        var answer = answerElement.GetString();
        if (string.IsNullOrEmpty(answer)) return null;
        var scalars = answer.EnumerateRunes().ToList();
        if (pattern.GetArrayLength() != scalars.Count) return null;

        var decoded = new List<string?>(scalars.Count);
        var blanks = 0;
        var position = 0;
        foreach (var element in pattern.EnumerateArray())
        {
            var expected = scalars[position++];
            if (element.ValueKind == JsonValueKind.Null)
            {
                if (!Rune.IsLetter(expected)) return null;
                blanks++;
                decoded.Add(null);
                continue;
            }
            if (element.ValueKind != JsonValueKind.String || element.GetString() != expected.ToString()) return null;
            decoded.Add(element.GetString());
        }
        return blanks == 0 ? null : new VocabGeneratedQuestion(direction, prompt, [], decoded, answer);
    }

    private static bool IsPartOfSpeech(string value) => value is "noun" or "verb" or "adjective" or "adverb"
        or "pronoun" or "preposition" or "conjunction" or "determiner" or "article" or "numeral" or "interjection";

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return true;
    }
}
