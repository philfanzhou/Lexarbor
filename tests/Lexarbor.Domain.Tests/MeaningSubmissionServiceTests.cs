using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class MeaningSubmissionServiceTests : TestBase
{
    private const string Source = "urn:test:client";
    private readonly IMeaningSubmissionRepository _submissions;

    public MeaningSubmissionServiceTests()
    {
        _submissions = new MeaningSubmissionRepository(_dbContext);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (MeaningSubmissionService Submissions, VocabularyDomainService Vocabulary) Create(string? bookId)
    {
        var settings = new MeaningSubmissionSettings { BookId = bookId };
        var vocabulary = new VocabularyDomainService(
            _vocabularyRepository, _bookRepository, _meaningRepository, _bookUnitRepository,
            _meaningUnitRepository, _unitOfWork, settings, _submissions);
        return (new MeaningSubmissionService(
            _submissions, _vocabularyRepository, _meaningRepository, vocabulary, _unitOfWork, settings,
            TimeProvider.System), vocabulary);
    }

    private async Task<(string Supplementary, string Main)> SeedAsync()
    {
        var supplementary = await CreateBookAsync();
        var main = await CreateBookAsync();
        var (_, vocabulary) = Create(supplementary.Id);
        foreach (var (word, meaning) in new[] { ("apple", "苹果"), ("river", "河流"), ("cloud", "云"), ("stone", "石头") })
        {
            await vocabulary.AddOrUpdateAsync(
                new VocabularyModel { Word = word },
                new VocabularyMeaningModel { BookId = main.Id, PartOfSpeech = "n.", Meaning = meaning });
        }

        return (supplementary.Id, main.Id);
    }

    [Fact]
    public async Task SubmitAsync_MergesEquivalentPendingSubmissions()
    {
        var (book, _) = await SeedAsync();
        var (service, _) = Create(book);

        var first = await service.SubmitAsync(Source, " Serendipity ", "N.", "意外发现", null, Ct);
        var second = await service.SubmitAsync(Source, "serendipity", "n.", "意外发现 ", "example", Ct);

        Assert.Equal(MeaningSubmissionStatus.Pending, first.Status);
        Assert.Equal("Serendipity", first.Word);
        Assert.Equal("n.", first.PartOfSpeech);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task SubmitAsync_IsApprovedAtOnceWhenTheSupplementaryBookHasTheDefinition()
    {
        var (book, _) = await SeedAsync();
        var (service, vocabulary) = Create(book);
        var (word, meaning) = await vocabulary.AddOrUpdateAsync(
            new VocabularyModel { Word = "zephyr" },
            new VocabularyMeaningModel { BookId = book, PartOfSpeech = "n.", Meaning = "西风" });

        var submission = await service.SubmitAsync(Source, "Zephyr", "n.", "西风", null, Ct);

        Assert.Equal(MeaningSubmissionStatus.Approved, submission.Status);
        Assert.Equal(meaning.Id, submission.ResultMeaningId);
        Assert.Equal(word.Id, submission.ResultWordId);
        Assert.True(submission.DefinitionKept);
    }

    [Fact]
    public async Task ApproveAsync_WritesTheDefinitionIntoTheSupplementaryBook()
    {
        var (book, _) = await SeedAsync();
        var (service, _) = Create(book);
        var pending = await service.SubmitAsync(Source, "serendipity", "n.", "意外发现", null, Ct);

        var approved = await service.ApproveAsync(pending.Id,
            new MeaningSubmissionApproval("serendipity", "n.", "意外发现", "A happy accident.", "ˌserənˈdɪpəti", null), Ct);

        Assert.Equal(MeaningSubmissionStatus.Approved, approved.Status);
        Assert.True(approved.DefinitionKept);
        Assert.Equal(book, approved.ResultBookId);
        var meaning = await _meaningRepository.GetByIdAsync(approved.ResultMeaningId!);
        Assert.Equal("意外发现", meaning!.Meaning);
        Assert.Equal(book, meaning.BookId);
        var word = await _vocabularyRepository.GetByIdAsync(approved.ResultWordId!);
        Assert.Equal("ˌserənˈdɪpəti", word!.PhoneticUk);

        var again = await service.SubmitAsync(Source, "serendipity", "n.", "意外发现", null, Ct);
        Assert.Equal(approved.Id, again.Id);
        await Assert.ThrowsAsync<ConflictException>(() => service.RejectAsync(approved.Id, Ct));
    }

    [Fact]
    public async Task ChangedOrRejectedSubmissions_HaveNoQuestion_AndCanBeProposedAgain()
    {
        var (book, _) = await SeedAsync();
        var (service, _) = Create(book);
        var changed = await service.SubmitAsync(Source, "serendipity", null, "运气", null, Ct);
        var rejected = await service.SubmitAsync(Source, "qwzx", null, "乱码", null, Ct);

        var approved = await service.ApproveAsync(changed.Id,
            new MeaningSubmissionApproval("serendipity", "n.", "意外发现", null, null, null), Ct);
        await service.RejectAsync(rejected.Id, Ct);

        Assert.False(approved.DefinitionKept);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.CreateQuestionAsync(changed.Id, true, Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.CreateQuestionAsync(rejected.Id, false, Ct));
        var resubmitted = await service.SubmitAsync(Source, "qwzx", null, "乱码", null, Ct);
        Assert.NotEqual(rejected.Id, resubmitted.Id);
        Assert.Equal(MeaningSubmissionStatus.Pending, resubmitted.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_AsksAPendingSubmissionWithCatalogDistractors(bool chineseToEnglish)
    {
        var (book, _) = await SeedAsync();
        var (service, _) = Create(book);
        var pending = await service.SubmitAsync(Source, "serendipity", "n.", "意外发现", null, Ct);

        var question = await service.CreateQuestionAsync(pending.Id, chineseToEnglish, Ct);

        Assert.Equal(chineseToEnglish ? "意外发现" : "serendipity", question.Word);
        Assert.Equal(4, question.Options.Count);
        var correct = Assert.Single(question.Options, option => option.IsCorrect);
        Assert.Equal(chineseToEnglish ? "serendipity" : "意外发现", correct.Text);
    }

    [Fact]
    public async Task CreateQuestionAsync_ForAKeptApproval_TopsUpTheSupplementaryBookFromTheCatalog()
    {
        var (book, _) = await SeedAsync();
        var (service, _) = Create(book);
        var pending = await service.SubmitAsync(Source, "serendipity", "n.", "意外发现", null, Ct);
        await service.ApproveAsync(pending.Id, new MeaningSubmissionApproval("serendipity", "n.", "意外发现", null, null, null), Ct);

        foreach (var chineseToEnglish in new[] { true, false })
        {
            var question = await service.CreateQuestionAsync(pending.Id, chineseToEnglish, Ct);
            Assert.Equal(4, question.Options.Count);
            Assert.Single(question.Options, option => option.IsCorrect);
        }
    }

    [Fact]
    public async Task OtherBooks_DoNotBorrowDistractors()
    {
        var (book, main) = await SeedAsync();
        var (_, vocabulary) = Create(book);
        var small = await CreateBookAsync();
        var (word, _) = await vocabulary.AddOrUpdateAsync(
            new VocabularyModel { Word = "lonely" },
            new VocabularyMeaningModel { BookId = small.Id, Meaning = "孤独的" });

        await Assert.ThrowsAsync<BusinessRuleException>(() => vocabulary.CreateQuestionAsync(word.Id, small.Id, true));
        Assert.NotEqual(main, small.Id);
    }

    [Fact]
    public async Task Submissions_AreRefusedWithoutASupplementaryBook()
    {
        await SeedAsync();
        var (service, _) = Create(null);

        Assert.False(service.IsConfigured);
        await Assert.ThrowsAsync<MeaningSubmissionsNotConfiguredException>(
            () => service.SubmitAsync(Source, "serendipity", null, "意外发现", null, Ct));
    }

    [Fact]
    public async Task Validation_RejectsMissingAndOversizedFields()
    {
        var (book, _) = await SeedAsync();
        var (service, _) = Create(book);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.SubmitAsync(Source, " ", null, "x", null, Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SubmitAsync(Source, "word", null, "", null, Ct));
        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SubmitAsync(Source, new string('a', 101), null, "x", null, Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.LookupAsync([], Ct));
        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.LookupAsync(Enumerable.Range(0, 101).Select(index => index.ToString()).ToList(), Ct));
    }

    [Fact]
    public async Task SearchCatalogAsync_AnswersEnabledMeaningsWithTheExactSpellingFirst()
    {
        var (book, main) = await SeedAsync();
        var (service, vocabulary) = Create(book);
        var disabled = await CreateBookAsync();
        await vocabulary.AddOrUpdateAsync(new VocabularyModel { Word = "Apples" },
            new VocabularyMeaningModel { BookId = main, Meaning = "复数苹果" });
        var (_, hidden) = await vocabulary.AddOrUpdateAsync(new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = disabled.Id, Meaning = "停用书里的苹果" });
        disabled.Status = false;
        await _bookRepository.UpdateAsync(disabled);
        await _unitOfWork.SaveChangesAsync(Ct);

        var matches = await service.SearchCatalogAsync(" APPLE ", Ct);

        Assert.Equal(["apple", "apples"], matches.Select(match => match.WordKey).Distinct());
        Assert.DoesNotContain(matches, match => match.MeaningId == hidden.Id);
        Assert.Equal("苹果", matches[0].MeaningKey);
    }

    [Fact]
    public void CatalogKeys_FoldAsciiOnly_LikeSqliteLower()
    {
        Assert.Equal("serendipity 意外", CatalogKeys.Of("  SerenDipity 意外 "));
        Assert.Equal("École", CatalogKeys.Of("École"));
    }
}
