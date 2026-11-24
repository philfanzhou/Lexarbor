using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Learning.Domain.Services;

/// <summary>One photo as uploaded: its bytes and declared media type. The host may keep recent photos for analysis.</summary>
public sealed record VocabPhoto(byte[] Content, string ContentType);

/// <summary>A word and the definition transcribed from the uploaded image.</summary>
public sealed record VocabPhotoEntry(string Word, string? PartOfSpeech, string Meaning, string? Review = null);

public interface IVocabularyPhotoRecognizer
{
    Task<IReadOnlyList<VocabPhotoEntry>> RecognizeAsync(VocabPhoto photo, CancellationToken cancellationToken = default);
}

/// <summary>Exact catalog meanings keyed by each requested spelling, with an empty list for a catalog miss.</summary>
public interface IVocabularyPhotoCatalog
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<VocabCatalogMeaning>>> LookupMeaningsAsync(
        IReadOnlyList<string> words, CancellationToken cancellationToken);
}

public enum VocabPhotoCandidateState
{
    /// <summary>There is exactly one distinct catalog meaning for the spelling.</summary>
    Matched,

    /// <summary>The word exists and needs the user to pick a meaning or supply a definition.</summary>
    Choose,

    /// <summary>The word is not in Lexarbor: the user supplies or confirms a definition for review.</summary>
    New,
}

/// <summary>
/// One preview candidate. Recognized is retained for wire compatibility. MeaningId is
/// the preselected meaning (Matched only); Meanings are the word's existing meanings to choose from. Meaning is empty
/// when the photo provides only the spelling.
/// </summary>
public sealed record VocabPhotoCandidate(
    string Word,
    string? Recognized,
    string? PartOfSpeech,
    string Meaning,
    VocabPhotoCandidateState State,
    string? MeaningId,
    IReadOnlyList<VocabCatalogMeaning> Meanings,
    string? Review = null);

/// <summary>Builds the notebook photo preview from transcribed entries and exact Lexarbor lookups.</summary>
public sealed class VocabularyPhotoImportService(
    ILearnerDirectory studentRepository,
    IVocabularyPhotoRecognizer recognizer,
    IVocabularyPhotoCatalog catalog,
    ILogger<VocabularyPhotoImportService>? logger = null)
{
    /// <summary>The largest photo accepted.</summary>
    public const int MaxPhotoBytes = 20 * 1024 * 1024;

    public async Task<IReadOnlyList<VocabPhotoCandidate>> PreviewAsync(Guid studentId, VocabPhoto photo, CancellationToken cancellationToken = default)
    {
        RequireImage(photo);
        if (!await studentRepository.ExistsAsync(studentId, cancellationToken).ConfigureAwait(false))
            throw new VocabStudentNotFoundException();

        var recognized = await recognizer.RecognizeAsync(photo, cancellationToken).ConfigureAwait(false);
        var entries = VocabularyPhotoEntries.Distinct(recognized);
        if (entries.Count == 0) return [];

        var spellings = entries.Select(entry => entry.Word).DistinctBy(VocabularyPhotoEntries.SpellingKey).ToArray();
        var lookups = await catalog.LookupMeaningsAsync(spellings, cancellationToken).ConfigureAwait(false);
        var bySpelling = spellings.ToDictionary(VocabularyPhotoEntries.SpellingKey,
            word => lookups[word].DistinctBy(meaning => meaning.MeaningKey, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var candidates = new List<VocabPhotoCandidate>();
        var senses = entries.ToLookup(entry => VocabularyPhotoEntries.SpellingKey(entry.Word));
        foreach (var entry in entries)
        {
            var spelling = VocabularyPhotoEntries.SpellingKey(entry.Word);
            var options = bySpelling[spelling];
            var state = options.Length switch
            {
                0 => VocabPhotoCandidateState.New,
                1 when !senses[spelling].Skip(1).Any() && entry.Review is null => VocabPhotoCandidateState.Matched,
                _ => VocabPhotoCandidateState.Choose,
            };
            var chosen = state == VocabPhotoCandidateState.Matched ? options[0] : null;
            candidates.Add(new VocabPhotoCandidate(chosen?.Word ?? entry.Word, null, entry.PartOfSpeech, entry.Meaning,
                state, chosen?.MeaningId, options, entry.Review));
        }

        logger?.LogInformation(
            "Vocabulary photo preview: {Entries} entries in {Spellings} distinct spellings, {Candidates} candidates, {Matched} matched.",
            entries.Count, spellings.Length, candidates.Count, candidates.Count(candidate => candidate.State == VocabPhotoCandidateState.Matched));
        return candidates;
    }

    /// <summary>A JPEG or PNG by signature, not empty and not above MaxPhotoBytes; otherwise VocabPhotoInvalidException.</summary>
    private static void RequireImage(VocabPhoto photo)
    {
        var bytes = photo.Content;
        if (bytes.Length is 0 or > MaxPhotoBytes) throw new VocabPhotoInvalidException();
        var jpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        if (!jpeg && !png) throw new VocabPhotoInvalidException();
    }
}

/// <summary>Not a JPEG or PNG, empty, or larger than VocabularyPhotoImportService.MaxPhotoBytes.</summary>
public sealed class VocabPhotoInvalidException() : Exception("The photo must be one JPEG or PNG image of at most 20 MB.");

/// <summary>Photo import is off, or the vision model is unavailable, timed out or answered unreadably.</summary>
public sealed class VocabPhotoImportUnavailableException(string? reason = null)
    : Exception(reason ?? "Photo import is temporarily unavailable.");
