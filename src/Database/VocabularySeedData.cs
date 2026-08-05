using System.Text;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Vocabulary.Database.Entities;

namespace Ruoyu.Study.Vocabulary.Database;

internal static class VocabularySeedData
{
    private const int RequiredEntryCount = 300;
    private const string StarterBookId = "starter-english-300";
    private const string ResourceSuffix = "SeedData.starter-vocabulary.tsv";

    public static async Task ApplyAsync(
        VocabularyDbContext context,
        CancellationToken cancellationToken)
    {
        var entries = await LoadAsync(cancellationToken);
        Validate(entries);

        var now = DateTimeOffset.UtcNow;
        var book = new VocabularyBookEntity
        {
            Id = StarterBookId,
            BookName = "Starter English 300",
            Description = "A self-authored starter wordbook for demonstrating vocabulary study and quiz generation.",
            Category = "Starter",
            DisplayOrder = 0,
            Status = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        var vocabularies = new List<VocabularyEntity>(entries.Count);
        var meanings = new List<VocabularyMeaningEntity>(entries.Count);
        foreach (var entry in entries)
        {
            var vocabularyId = Guid.NewGuid().ToString();
            vocabularies.Add(new VocabularyEntity
            {
                Id = vocabularyId,
                Word = entry.Word.Trim().ToLowerInvariant(),
                PhoneticUk = entry.PhoneticUk.Trim(),
                PhoneticUs = entry.PhoneticUs.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            });
            meanings.Add(new VocabularyMeaningEntity
            {
                Id = Guid.NewGuid().ToString(),
                VocabularyId = vocabularyId,
                BookId = StarterBookId,
                PartOfSpeech = entry.PartOfSpeech.Trim().ToLowerInvariant(),
                Meaning = entry.Meaning.Trim(),
                Example = null,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await using var transaction = await context.Database.BeginTransactionAsync(
            cancellationToken);
        await context.VocabularyBooks.AddAsync(book, cancellationToken);
        await context.Vocabularies.AddRangeAsync(vocabularies, cancellationToken);
        await context.VocabularyMeanings.AddRangeAsync(meanings, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<List<SeedEntry>> LoadAsync(
        CancellationToken cancellationToken)
    {
        var assembly = typeof(VocabularySeedData).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resourceName == null)
        {
            throw new InvalidOperationException(
                "The bundled starter vocabulary resource was not found.");
        }

        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "The bundled starter vocabulary resource could not be opened.");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        var entries = new List<SeedEntry>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var columns = line.Split('\t');
            if (columns.Length != 5)
            {
                throw new InvalidOperationException(
                    $"The bundled starter vocabulary contains an invalid row: {line}.");
            }

            entries.Add(new SeedEntry(
                columns[0],
                columns[1],
                columns[2],
                columns[3],
                columns[4]));
        }

        return entries;
    }

    private static void Validate(IReadOnlyCollection<SeedEntry> entries)
    {
        if (entries.Count != RequiredEntryCount)
        {
            throw new InvalidOperationException(
                $"The bundled starter vocabulary must contain exactly {RequiredEntryCount} entries.");
        }

        var duplicate = entries
            .GroupBy(entry => entry.Word.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException(
                $"The bundled starter vocabulary contains a duplicate word: {duplicate.Key}.");
        }

        if (entries.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Word) ||
                string.IsNullOrWhiteSpace(entry.PhoneticUk) ||
                string.IsNullOrWhiteSpace(entry.PhoneticUs) ||
                string.IsNullOrWhiteSpace(entry.PartOfSpeech) ||
                string.IsNullOrWhiteSpace(entry.Meaning)))
        {
            throw new InvalidOperationException(
                "Every bundled starter entry must include a word, UK and US phonetics, part of speech, and meaning.");
        }
    }

    private sealed record SeedEntry(
        string Word,
        string PhoneticUk,
        string PhoneticUs,
        string PartOfSpeech,
        string Meaning);
}
