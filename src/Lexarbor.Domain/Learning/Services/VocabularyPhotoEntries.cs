namespace Lexarbor.Learning.Domain.Services;

internal static class VocabularyPhotoEntries
{
    internal static IReadOnlyList<VocabPhotoEntry> Distinct(IReadOnlyList<VocabPhotoEntry> entries)
    {
        var withMeaning = entries.Where(entry => entry.Meaning.Length > 0).Select(entry => SpellingKey(entry.Word)).ToHashSet();
        return entries.Where(entry => entry.Meaning.Length > 0 || !withMeaning.Contains(SpellingKey(entry.Word)))
            .DistinctBy(entry => (SpellingKey(entry.Word), entry.Meaning))
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
