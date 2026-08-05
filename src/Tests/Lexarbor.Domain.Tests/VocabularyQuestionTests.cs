using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class VocabularyQuestionTests : TestBase
{
    private readonly VocabularyDomainService _service;

    public VocabularyQuestionTests()
    {
        _service = new VocabularyDomainService(
            _vocabularyRepository,
            _bookRepository,
            _meaningRepository,
            _unitOfWork);
    }

    [Fact]
    public async Task CreateQuestionAsync_ChineseToEnglish_UsesOnlyCurrentBook()
    {
        var data = await SeedCompleteBookAsync();

        var question = await _service.CreateQuestionAsync(
            data.CorrectWord.Id,
            data.Book.Id,
            chineseToEnglish: true);

        Assert.Equal("fruit", question.Word);
        AssertQuestionOptions(question, ["apple", "banana", "cherry", "date"]);
        Assert.DoesNotContain(question.Options, option => option.Text == "crossbook-only");
    }

    [Fact]
    public async Task CreateQuestionAsync_EnglishToChinese_UsesOnlyCurrentBook()
    {
        var data = await SeedCompleteBookAsync();

        var question = await _service.CreateQuestionAsync(
            data.CorrectWord.Id,
            data.Book.Id,
            chineseToEnglish: false);

        Assert.Equal("apple", question.Word);
        AssertQuestionOptions(question, ["fruit", "yellow fruit", "red fruit", "sweet fruit"]);
        Assert.DoesNotContain(question.Options, option => option.Text == "cross-book meaning");
    }

    [Fact]
    public async Task CreateQuestionAsync_ExcludesCorrectAnswer()
    {
        var data = await SeedCompleteBookAsync();

        var question = await _service.CreateQuestionAsync(
            data.CorrectWord.Id,
            data.Book.Id,
            chineseToEnglish: true);

        Assert.Single(question.Options, option => option.IsCorrect);
        Assert.Single(question.Options, option => option.Text == data.CorrectWord.Word);
    }

    [Fact]
    public async Task CreateQuestionAsync_DeduplicatesWordsWithMultipleMeanings()
    {
        var data = await SeedCompleteBookAsync(addSecondMeaningToDistractor: true);

        var question = await _service.CreateQuestionAsync(
            data.CorrectWord.Id,
            data.Book.Id,
            chineseToEnglish: true);

        AssertQuestionOptions(question, ["apple", "banana", "cherry", "date"]);
    }

    [Fact]
    public async Task CreateQuestionAsync_EnglishToChinese_DeduplicatesMeaningTextBeforeLimit()
    {
        var book = await CreateBookAsync();
        var correct = await SeedWordAsync(book.Id, "apple", "fruit");
        _ = await SeedWordAsync(book.Id, "banana", "shared meaning");
        _ = await SeedWordAsync(book.Id, "cherry", "shared meaning");
        _ = await SeedWordAsync(book.Id, "date", "red fruit");
        _ = await SeedWordAsync(book.Id, "elderberry", "sweet fruit");

        var question = await _service.CreateQuestionAsync(
            correct.Id,
            book.Id,
            chineseToEnglish: false);

        AssertQuestionOptions(
            question,
            ["fruit", "shared meaning", "red fruit", "sweet fruit"]);
    }

    [Fact]
    public async Task CreateQuestionAsync_EnglishToChinese_UsesAlternativeMeaningWhenCorrectTextMatches()
    {
        var book = await CreateBookAsync();
        var correct = await SeedWordAsync(book.Id, "apple", "fruit");
        var banana = await SeedWordAsync(book.Id, "banana", "fruit");
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Id = banana.Id, Word = banana.Word },
            new VocabularyMeaningModel
            {
                BookId = book.Id,
                Meaning = "yellow fruit"
            });
        _ = await SeedWordAsync(book.Id, "cherry", "red fruit");
        _ = await SeedWordAsync(book.Id, "date", "sweet fruit");

        var question = await _service.CreateQuestionAsync(
            correct.Id,
            book.Id,
            chineseToEnglish: false);

        AssertQuestionOptions(
            question,
            ["fruit", "yellow fruit", "red fruit", "sweet fruit"]);
    }

    [Fact]
    public async Task CreateQuestionAsync_ChineseToEnglish_ExcludesHistoricalEquivalentWord()
    {
        var book = await CreateBookAsync();
        var correct = await SeedWordAsync(book.Id, "apple", "fruit");
        var now = DateTimeOffset.UtcNow;
        var historicalDuplicate = new VocabularyModel
        {
            Id = Guid.NewGuid().ToString(),
            Word = " APPLE ",
            CreatedAt = now,
            UpdatedAt = now
        };
        await _vocabularyRepository.AddAsync(historicalDuplicate);
        await _meaningRepository.AddAsync(new VocabularyMeaningModel
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyId = historicalDuplicate.Id,
            BookId = book.Id,
            Meaning = "historical duplicate",
            CreatedAt = now,
            UpdatedAt = now
        });
        _ = await SeedWordAsync(book.Id, "banana", "yellow fruit");
        _ = await SeedWordAsync(book.Id, "cherry", "red fruit");
        _ = await SeedWordAsync(book.Id, "date", "sweet fruit");
        await _unitOfWork.SaveChangesAsync();

        var question = await _service.CreateQuestionAsync(
            correct.Id,
            book.Id,
            chineseToEnglish: true);

        AssertQuestionOptions(question, ["apple", "banana", "cherry", "date"]);
        Assert.Equal(
            4,
            question.Options
                .Select(option => option.Text.Trim().ToLowerInvariant())
                .Distinct()
                .Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_FewerThanThreeDistractors_ThrowsBusinessRule(
        bool chineseToEnglish)
    {
        var book = await CreateBookAsync();
        var correct = await SeedWordAsync(book.Id, "apple", "fruit");
        _ = await SeedWordAsync(book.Id, "banana", "yellow fruit");
        _ = await SeedWordAsync(book.Id, "cherry", "red fruit");

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            _service.CreateQuestionAsync(correct.Id, book.Id, chineseToEnglish));

        Assert.Equal(
            "The vocabulary book does not contain enough distinct words to create a question.",
            exception.Message);
    }

    [Fact]
    public async Task CreateQuestionAsync_DisabledBook_ThrowsBusinessRule()
    {
        var data = await SeedCompleteBookAsync();
        data.Book.Status = false;
        await _bookRepository.UpdateAsync(data.Book);
        await _unitOfWork.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            _service.CreateQuestionAsync(data.CorrectWord.Id, data.Book.Id, chineseToEnglish: true));
    }

    private async Task<QuestionSeedData> SeedCompleteBookAsync(
        bool addSecondMeaningToDistractor = false)
    {
        var book = await CreateBookAsync();
        var correctWord = await SeedWordAsync(book.Id, "apple", "fruit");
        var banana = await SeedWordAsync(book.Id, "banana", "yellow fruit");
        _ = await SeedWordAsync(book.Id, "cherry", "red fruit");
        _ = await SeedWordAsync(book.Id, "date", "sweet fruit");

        if (addSecondMeaningToDistractor)
        {
            await _service.AddOrUpdateAsync(
                new VocabularyModel { Id = banana.Id, Word = banana.Word },
                new VocabularyMeaningModel
                {
                    BookId = book.Id,
                    PartOfSpeech = "noun",
                    Meaning = "curved fruit"
                });
        }

        var otherBook = await CreateBookAsync();
        _ = await SeedWordAsync(otherBook.Id, "crossbook-only", "cross-book meaning");
        return new QuestionSeedData(book, correctWord);
    }

    private async Task<VocabularyModel> SeedWordAsync(
        string bookId,
        string word,
        string meaning)
    {
        var (createdWord, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = word },
            new VocabularyMeaningModel
            {
                BookId = bookId,
                PartOfSpeech = "noun",
                Meaning = meaning
            });
        return createdWord;
    }

    private static void AssertQuestionOptions(
        VocabularyQuestionModel question,
        IReadOnlyCollection<string> expectedOptions)
    {
        Assert.Equal(4, question.Options.Count);
        Assert.Equal(4, question.Options.Select(option => option.Text).Distinct().Count());
        Assert.Single(question.Options, option => option.IsCorrect);
        Assert.Equal(
            expectedOptions.OrderBy(value => value),
            question.Options.Select(option => option.Text).OrderBy(value => value));
    }

    private sealed record QuestionSeedData(
        VocabularyBookModel Book,
        VocabularyModel CorrectWord);
}
