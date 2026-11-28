namespace Lexarbor.Learning.Domain.Services;

/// <summary>Not a JPEG or PNG, empty, or larger than VocabularyPhotoImportService.MaxPhotoBytes.</summary>
public sealed class VocabPhotoInvalidException() : Exception("The photo must be one JPEG or PNG image of at most 20 MB.");

/// <summary>Photo import is off, or the vision model is unavailable, timed out or answered unreadably.</summary>
public sealed class VocabPhotoUnavailableException(string? reason = null)
    : Exception(reason ?? "Photo import is temporarily unavailable.");
