namespace Lexarbor.Domain.Models;

/// <summary>
/// Outcome of one batch import. <see cref="Created"/> counts the meanings the
/// batch inserted; <see cref="Reused"/> counts the entries that matched an
/// equivalent meaning already stored or written earlier in the same batch.
/// </summary>
public sealed record VocabularyBatchImportResult(int Total, int Created, int Reused);
