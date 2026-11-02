using System.Text.Json;
using System.Text.Json.Serialization;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Service.Dtos;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyMeaningEditRequest
{
    public required string? PartOfSpeech { get; init; }
    public required string? Meaning { get; init; }
    public required string? Example { get; init; }
    // Undefined is omission; JSON null is an explicit clear.
    public JsonElement PhoneticUk { get; init; }
    public JsonElement PhoneticUs { get; init; }
    private VocabularyMeaningPhoneticConditionDto? _expectedUk;
    private VocabularyMeaningPhoneticConditionDto? _expectedUs;
    private bool _expectedUkProvided;
    private bool _expectedUsProvided;
    public VocabularyMeaningPhoneticConditionDto? ExpectedUk
    {
        get => _expectedUk;
        init { _expectedUk = value; _expectedUkProvided = true; }
    }
    public VocabularyMeaningPhoneticConditionDto? ExpectedUs
    {
        get => _expectedUs;
        init { _expectedUs = value; _expectedUsProvided = true; }
    }

    public IReadOnlyList<MeaningPhoneticEdit> Edits(string bookId, string wordId, string meaningId)
    {
        var edits = new List<MeaningPhoneticEdit>();
        Add(PhoneticUk, ExpectedUk, _expectedUkProvided, AudioLocale.EnGb);
        Add(PhoneticUs, ExpectedUs, _expectedUsProvided, AudioLocale.EnUs);
        return edits;

        void Add(JsonElement value, VocabularyMeaningPhoneticConditionDto? expected, bool conditionProvided, AudioLocale locale)
        {
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                if (conditionProvided) throw new DomainValidationException("A phonetic condition requires its phonetic value.");
                return;
            }
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new DomainValidationException("Specific phonetics must be strings or null.");
            if (expected == null || expected.Epoch < 0 || expected.SourceRevision == null || expected.SourceRevision.Length != 64 ||
                expected.SourceRevision.Any(character => !char.IsAsciiHexDigitLower(character)) ||
                expected.OwnAssetId is { } asset && (string.IsNullOrWhiteSpace(asset) || asset.Length > 128 || asset.Any(char.IsControl)))
                throw new DomainValidationException("A valid expected phonetic condition is required for each provided value.");
            var text = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            if (text?.Length > 512 || text?.Any(char.IsControl) == true)
                throw new DomainValidationException("Specific phonetic must be at most 512 characters without control characters.");
            edits.Add(new(bookId, wordId, meaningId, locale, true, text,
                new(null, expected.Epoch, expected.SourceRevision, expected.OwnAssetId)));
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VocabularyMeaningPhoneticConditionDto
{
    public required long Epoch { get; init; }
    public required string SourceRevision { get; init; }
    public required string? OwnAssetId { get; init; }
}
