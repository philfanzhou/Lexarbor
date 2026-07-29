using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.Vocabulary.Database;

public static class VocabularyDataIntegrityDiagnostics
{
    public static async Task<VocabularyDataIntegrityReport> InspectAndLogAsync(
        VocabularyDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var nullBookIdCount = await context.VocabularyMeanings
            .CountAsync(meaning => meaning.BookId == null, cancellationToken);

        var orphanBookIdCount = await context.VocabularyMeanings
            .CountAsync(
                meaning => meaning.BookId != null &&
                           !context.VocabularyBooks.Any(book => book.Id == meaning.BookId),
                cancellationToken);

        var normalizedDuplicateWordGroupCount = await context.Vocabularies
            .GroupBy(vocabulary => vocabulary.Word.Trim().ToLower())
            .Where(group => group.Count() > 1)
            .CountAsync(cancellationToken);

        var duplicateMeaningGroupCount = await context.VocabularyMeanings
            .GroupBy(meaning => new
            {
                meaning.VocabularyId,
                meaning.BookId,
                PartOfSpeech = (meaning.PartOfSpeech ?? string.Empty).Trim().ToLower(),
                Meaning = meaning.Meaning.Trim()
            })
            .Where(group => group.Count() > 1)
            .CountAsync(cancellationToken);

        var report = new VocabularyDataIntegrityReport(
            nullBookIdCount,
            orphanBookIdCount,
            normalizedDuplicateWordGroupCount,
            duplicateMeaningGroupCount);

        if (report.HasIssues)
        {
            logger.LogWarning(
                "Vocabulary data integrity anomalies detected: NullBookIds={NullBookIdCount}, OrphanBookIds={OrphanBookIdCount}, DuplicateWordGroups={NormalizedDuplicateWordGroupCount}, DuplicateMeaningGroups={DuplicateMeaningGroupCount}",
                report.NullBookIdCount,
                report.OrphanBookIdCount,
                report.NormalizedDuplicateWordGroupCount,
                report.DuplicateMeaningGroupCount);
        }
        else
        {
            logger.LogInformation(
                "Vocabulary data integrity diagnostics passed: NullBookIds=0, OrphanBookIds=0, DuplicateWordGroups=0, DuplicateMeaningGroups=0");
        }

        return report;
    }
}
