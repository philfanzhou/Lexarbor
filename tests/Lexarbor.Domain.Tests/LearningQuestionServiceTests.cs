using System.Reflection;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class LearningQuestionServiceTests : TestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private VocabularyDomainService Catalog(LearningQuestionGenerator generator, IVocabularyRepository? words = null,
        IVocabularyMeaningRepository? meanings = null, MeaningSubmissionSettings? settings = null, IMeaningSubmissionRepository? submissions = null) => new(
        words ?? _vocabularyRepository, _bookRepository, meanings ?? _meaningRepository, _bookUnitRepository, _meaningUnitRepository,
        _unitOfWork, settings, submissions, generator);

    private static LearningQuestionGenerator PreferLast() => new(max => max - 1);
    private static LearningQuestionGenerator PreferFirst() => new(_ => 0);

    private async Task<(string Word, string Meaning)> Seed(VocabularyDomainService service, string book, string word = "explode", string meaning = "爆炸")
    {
        var target = await service.AddOrUpdateAsync(new() { Word = word }, new() { BookId = book, Meaning = meaning });
        return (target.word.Id, target.meaning.Id);
    }

    [Fact]
    public async Task CatalogFillLettersDoesNotReadEitherDistractorRepositoryAndWritesNothing()
    {
        var service = Catalog(PreferLast());
        var book = await CreateBookAsync();
        var target = await Seed(service, book.Id, "𐐀𐐁𐐂-𐐃𐐄𐐅");
        var words = Probe<IVocabularyRepository>(_vocabularyRepository, method => method.StartsWith("GetRandom"));
        var meanings = Probe<IVocabularyMeaningRepository>(_meaningRepository, method => method.StartsWith("GetRandom"));
        service = Catalog(PreferLast(), words, meanings);
        var before = await Changes();
        var fill = Assert.IsType<FillLettersLearningQuestion>(await service.CreateLearningQuestionAsync(
            target.Word, book.Id, target.Meaning, LearningQuestionMode.Review, cancellationToken: Ct));
        Assert.Equal(7, fill.Pattern.Count);
        Assert.Equal(2, fill.Pattern.Count(item => item is null));
        Assert.Equal(before, await Changes());
        Assert.DoesNotContain(_dbContext.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    [Fact]
    public async Task CatalogNoDistractorsReviewFallsBackWithinRequestButNewFailsAndShortWordFails()
    {
        var service = Catalog(PreferFirst());
        var book = await CreateBookAsync();
        var target = await Seed(service, book.Id);
        Assert.IsType<FillLettersLearningQuestion>(await service.CreateLearningQuestionAsync(
            target.Word, book.Id, target.Meaning, LearningQuestionMode.Review, cancellationToken: Ct));
        var failure = await Assert.ThrowsAsync<LearningQuestionException>(() => service.CreateLearningQuestionAsync(
            target.Word, book.Id, target.Meaning, LearningQuestionMode.New, cancellationToken: Ct));
        Assert.Equal(LearningQuestionFailure.Unavailable, failure.Failure);
        var shortWord = await Seed(service, book.Id, "go", "去");
        await Assert.ThrowsAsync<LearningQuestionException>(() => service.CreateLearningQuestionAsync(
            shortWord.Word, book.Id, shortWord.Meaning, LearningQuestionMode.Review, cancellationToken: Ct));
    }

    [Fact]
    public async Task RealCatalogPartialDirectionAvailabilityUsesOnlyTheAvailableDirection()
    {
        var service = Catalog(PreferFirst());
        var book = await CreateBookAsync();
        var target = await Seed(service, book.Id, "go", "去");
        // Three distinct words, but one shared definition: only ChineseToEnglish is possible.
        foreach (var word in new[] { "aa", "bb", "cc" }) await Seed(service, book.Id, word, "相同");
        var question = Assert.IsType<ChoiceLearningQuestion>(await service.CreateLearningQuestionAsync(
            target.Word, book.Id, target.Meaning, LearningQuestionMode.Review, cancellationToken: Ct));
        Assert.Equal(LearningQuestionType.ChineseToEnglish, question.Type);
        Assert.Equal(4, question.Options.Count);
        await Assert.ThrowsAsync<LearningQuestionException>(() => service.CreateLearningQuestionAsync(
            target.Word, book.Id, target.Meaning, LearningQuestionMode.New, cancellationToken: Ct));
    }

    [Fact]
    public async Task SubmissionPendingHasIndependentFillAndApprovedUsesTheCurrentCatalogAndKeptGate()
    {
        var supplementary = await CreateBookAsync();
        var settings = new MeaningSubmissionSettings { BookId = supplementary.Id };
        var repository = new MeaningSubmissionRepository(_dbContext);
        var generator = PreferLast();
        var vocabulary = Catalog(generator, settings: settings, submissions: repository);
        var pending = await new MeaningSubmissionService(repository, _vocabularyRepository, _meaningRepository, vocabulary,
            _unitOfWork, settings, TimeProvider.System).SubmitAsync("test", "Explode", null, "爆炸", null, Ct);
        var guarded = Probe<IMeaningSubmissionRepository>(repository, method => method.StartsWith("GetCatalog"));
        var service = new MeaningSubmissionService(guarded, _vocabularyRepository, _meaningRepository, vocabulary,
            _unitOfWork, settings, TimeProvider.System, learningQuestions: generator);
        var before = await Changes();
        var fill = Assert.IsType<FillLettersLearningQuestion>(await service.CreateLearningQuestionAsync(pending.Id, LearningQuestionMode.Review, Ct));
        Assert.Equal("Explode", fill.AnswerWord);
        Assert.Equal(before, await Changes());
        service = new MeaningSubmissionService(repository, _vocabularyRepository, _meaningRepository, vocabulary,
            _unitOfWork, settings, TimeProvider.System, learningQuestions: generator);
        var failure = await Assert.ThrowsAsync<LearningQuestionException>(() => service.CreateLearningQuestionAsync(pending.Id, LearningQuestionMode.New, Ct));
        Assert.Equal(LearningQuestionFailure.Unavailable, failure.Failure);
        var approved = await service.ApproveAsync(pending.Id, new("Explode", null, "爆炸", null, null, null), Ct);
        Assert.True(approved.DefinitionKept);
        Assert.IsType<FillLettersLearningQuestion>(await service.CreateLearningQuestionAsync(pending.Id, LearningQuestionMode.Review, Ct));
        // Even POS/IPA-only AI revise must remain unavailable with identical word/meaning.
        approved.AiReviewVerdict = "revise";
        await repository.UpdateAsync(approved, Ct);
        await _unitOfWork.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.CreateLearningQuestionAsync(pending.Id, LearningQuestionMode.Review, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogReadCancellationAndStorageFaultDoNotFallBack(bool cancel)
    {
        var service = Catalog(PreferFirst());
        var book = await CreateBookAsync();
        var target = await Seed(service, book.Id);
        var calls = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var proxy = DispatchProxy.Create<IVocabularyMeaningRepository, ReadProbe>();
        var failure = new InvalidOperationException("simulated database fault");
        ((ReadProbe)(object)proxy).Read = (method, args) =>
        {
            if (method.Name.StartsWith("GetRandom"))
            {
                calls++;
                Assert.Equal(cancellation.Token, args![^1]);
                if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                throw failure;
            }
            return method.Invoke(_meaningRepository, args);
        };
        service = Catalog(PreferFirst(), meanings: proxy);
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => service.CreateLearningQuestionAsync(
            target.Word, book.Id, target.Meaning, LearningQuestionMode.Review, cancellationToken: cancellation.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(actual);
        else Assert.Same(failure, actual);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSubmissionCandidateFaultAndCancellationDoNotFallBack(bool cancel)
    {
        var book = await CreateBookAsync();
        var settings = new MeaningSubmissionSettings { BookId = book.Id };
        var repository = new MeaningSubmissionRepository(_dbContext);
        var vocabulary = Catalog(PreferFirst(), settings: settings, submissions: repository);
        var service = new MeaningSubmissionService(repository, _vocabularyRepository, _meaningRepository, vocabulary,
            _unitOfWork, settings, TimeProvider.System, learningQuestions: PreferFirst());
        var pending = await service.SubmitAsync("test", "explode", null, "爆炸", null, Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var calls = 0;
        var proxy = DispatchProxy.Create<IMeaningSubmissionRepository, ReadProbe>();
        var failure = new InvalidOperationException("simulated database fault");
        ((ReadProbe)(object)proxy).Read = (method, args) =>
        {
            if (method.Name.StartsWith("GetCatalog"))
            {
                calls++;
                Assert.Equal(cancellation.Token, args![^1]);
                if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                throw failure;
            }
            return method.Invoke(repository, args);
        };
        service = new MeaningSubmissionService(proxy, _vocabularyRepository, _meaningRepository, vocabulary,
            _unitOfWork, settings, TimeProvider.System, learningQuestions: PreferFirst());
        var before = await Changes();
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => service.CreateLearningQuestionAsync(
            pending.Id, LearningQuestionMode.Review, cancellation.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(actual);
        else Assert.Same(failure, actual);
        Assert.Equal(1, calls);
        Assert.Equal(before, await Changes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringTargetReadCannotBecomeTargetNotFound(bool submissionTarget)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var book = await CreateBookAsync();
        if (submissionTarget)
        {
            var proxy = DispatchProxy.Create<IMeaningSubmissionRepository, ReadProbe>();
            ((ReadProbe)(object)proxy).Read = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<MeaningSubmissionModel?>(null);
            };
            var service = new MeaningSubmissionService(proxy, _vocabularyRepository, _meaningRepository, Catalog(PreferLast()),
                _unitOfWork, new() { BookId = book.Id }, TimeProvider.System);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateLearningQuestionAsync("target", LearningQuestionMode.Review, cancellation.Token));
        }
        else
        {
            var proxy = DispatchProxy.Create<IVocabularyRepository, ReadProbe>();
            ((ReadProbe)(object)proxy).Read = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<VocabularyModel?>(null);
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog(PreferLast(), words: proxy).CreateLearningQuestionAsync(
                "target", book.Id, "meaning", LearningQuestionMode.Review, cancellationToken: cancellation.Token));
        }
    }

    private async Task<long> Changes()
    {
        using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT total_changes()";
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    private static T Probe<T>(T inner, Func<string, bool> forbidden) where T : class
    {
        var proxy = DispatchProxy.Create<T, ReadProbe>();
        ((ReadProbe)(object)proxy).Read = (method, args) => forbidden(method.Name)
            ? throw new InvalidOperationException("Distractor read is forbidden.") : method.Invoke(inner, args);
        return proxy;
    }

    public class ReadProbe : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Read { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Read(method!, args);
    }
}
