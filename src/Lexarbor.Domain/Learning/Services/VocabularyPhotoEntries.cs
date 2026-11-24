namespace Lexarbor.Learning.Domain.Services;

internal static class VocabularyPhotoEntries
{
    internal static IReadOnlyList<VocabPhotoEntry> Distinct(IReadOnlyList<VocabPhotoEntry> entries)
    {
        return entries.DistinctBy(entry => (SpellingKey(entry.Word), entry.PartOfSpeech, entry.Meaning, entry.Review))
            .ToArray();
    }

    // Match Lexarbor's ASCII case folding while retaining the transcribed spelling for a miss.
    internal static string SpellingKey(string word) => string.Create(word.Length, word, static (key, source) =>
    {
        for (var index = 0; index < source.Length; index++)
        {
            var letter = source[index];
            key[index] = letter is >= 'A' and <= 'Z' ? (char)(letter + ('a' - 'A')) : letter;
        }
    });
}
