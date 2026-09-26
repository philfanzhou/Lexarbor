namespace Lexarbor.Domain.Models;

public sealed record VocabularyAdminBook(string Id, string BookName, bool Status);
public sealed record VocabularyAdminWord(VocabularyModel Word, IReadOnlyList<VocabularyAdminBook> Books,
    IReadOnlyList<VocabularyMeaningModel> Meanings);
public sealed record VocabularyAdminPage(IReadOnlyList<VocabularyAdminWord> Items, int TotalCount, int TotalPage);
public sealed record VocabularyAdminContent(VocabularyBookModel Book, int WordCount, int MeaningCount,
    VocabularyAdminPage Page);
