namespace Ruoyu.Study.Vocabulary.Database;

public sealed record VocabularyDataIntegrityReport(
    int NullBookIdCount,
    int OrphanBookIdCount,
    int NormalizedDuplicateWordGroupCount,
    int DuplicateMeaningGroupCount)
{
    public bool HasIssues =>
        NullBookIdCount > 0 ||
        OrphanBookIdCount > 0 ||
        NormalizedDuplicateWordGroupCount > 0 ||
        DuplicateMeaningGroupCount > 0;
}
