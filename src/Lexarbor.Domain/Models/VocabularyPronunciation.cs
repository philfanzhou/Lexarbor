namespace Lexarbor.Domain.Models;

/// <summary>The effective display phonetic and its explicit inheritance source.</summary>
public sealed record VocabularyPronunciationAccent(string? Phonetic, string Source);

/// <summary>Read-only pronunciation metadata, independent of audio availability.</summary>
public sealed record VocabularyPronunciation(VocabularyPronunciationAccent Uk, VocabularyPronunciationAccent Us);
