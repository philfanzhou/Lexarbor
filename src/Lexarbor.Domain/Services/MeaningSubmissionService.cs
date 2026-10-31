using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

/// <summary>
/// Definitions proposed by integration clients and reviewed by administrators.
/// A submission is held outside every book while pending; approval writes the
/// reviewed definition into the one supplementary book the deployment names.
/// </summary>
public sealed class MeaningSubmissionService
{
    public const int MaxWordLength = 100;
    public const int MaxMeaningLength = 500;
    public const int MaxPartOfSpeechLength = 32;
    public const int MaxExampleLength = 1000;
    public const int MaxLookupIds = 100;

    private readonly IMeaningSubmissionRepository _submissions;
    private readonly IVocabularyRepository _vocabularyRepository;
    private readonly IVocabularyMeaningRepository _meaningRepository;
    private readonly VocabularyDomainService _vocabularyService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly MeaningSubmissionSettings _settings;
    private readonly IMeaningSubmissionReviewAssistant? _assistant;
    private readonly TimeProvider _time;

    public MeaningSubmissionService(
        IMeaningSubmissionRepository submissions,
        IVocabularyRepository vocabularyRepository,
        IVocabularyMeaningRepository meaningRepository,
        VocabularyDomainService vocabularyService,
        IUnitOfWork unitOfWork,
        MeaningSubmissionSettings settings,
        TimeProvider time,
        IMeaningSubmissionReviewAssistant? assistant = null)
    {
        _submissions = submissions;
        _vocabularyRepository = vocabularyRepository;
        _meaningRepository = meaningRepository;
        _vocabularyService = vocabularyService;
        _unitOfWork = unitOfWork;
        _settings = settings;
        _time = time;
        _assistant = assistant;
    }

    public bool IsConfigured => _settings.IsConfigured;

    public string? BookId => _settings.IsConfigured ? _settings.BookId!.Trim() : null;

    public bool ReviewAssistantEnabled => _assistant?.IsEnabled == true;

    /// <summary>
    /// Records a proposed definition and answers its submission. An equivalent
    /// submission that is pending, or approved without a changed spelling or
    /// definition, is answered instead of a new one; an equivalent definition
    /// already in the supplementary book answers an approved submission at once.
    /// </summary>
    public async Task<MeaningSubmissionModel> SubmitAsync(
        string source,
        string? word,
        string? partOfSpeech,
        string? meaning,
        string? example,
        CancellationToken cancellationToken = default)
    {
        var bookId = RequireBook();
        var spelling = Required(word, MaxWordLength, "Word");
        var definition = Required(meaning, MaxMeaningLength, "Meaning");
        var normalizedPartOfSpeech = Optional(partOfSpeech, MaxPartOfSpeechLength, "Part of speech")?.ToLowerInvariant();
        var normalizedExample = Optional(example, MaxExampleLength, "Example");
        var normalizedWord = spelling.ToLowerInvariant();

        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var reusable = await _submissions.FindReusableAsync(
                normalizedWord, normalizedPartOfSpeech ?? string.Empty, definition, cancellationToken);
            if (reusable != null)
            {
                return reusable;
            }

            var now = _time.GetUtcNow();
            var submission = new MeaningSubmissionModel
            {
                Id = Guid.NewGuid().ToString(),
                Source = source,
                Word = spelling,
                PartOfSpeech = normalizedPartOfSpeech,
                Meaning = definition,
                Example = normalizedExample,
                Status = MeaningSubmissionStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            };

            var existingWord = await _vocabularyRepository.GetByNormalizedWordAsync(normalizedWord);
            var existingMeaning = existingWord == null
                ? null
                : await _meaningRepository.GetEquivalentAsync(
                    existingWord.Id, bookId, normalizedPartOfSpeech ?? string.Empty, definition);
            if (existingWord != null && existingMeaning != null)
            {
                MarkApproved(submission, existingWord, existingMeaning, now);
            }

            await _submissions.AddAsync(submission, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return submission;
        }, cancellationToken);
    }

    /// <summary>
    /// The meanings an integration client can reference instead of submitting:
    /// every enabled book's meanings of the words matching the keyword, the
    /// supplementary book included.
    /// </summary>
    public Task<List<CatalogMeaningMatch>> SearchCatalogAsync(string? keyword, CancellationToken cancellationToken = default)
    {
        var normalized = CatalogKeys.Of(Required(keyword, MaxWordLength, "Keyword"));
        return _submissions.SearchCatalogAsync(normalized, 10, cancellationToken);
    }

    public async Task<List<MeaningSubmissionModel>> LookupAsync(
        IReadOnlyCollection<string>? ids,
        CancellationToken cancellationToken = default)
    {
        var distinct = (ids ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
        {
            throw new DomainValidationException("At least one submission ID is required.");
        }

        if (distinct.Count > MaxLookupIds)
        {
            throw new DomainValidationException($"At most {MaxLookupIds} submission IDs can be looked up at once.");
        }

        return await _submissions.GetByIdsAsync(distinct, cancellationToken);
    }

    /// <summary>
    /// A four-option question about one submission. A pending submission is asked
    /// with its own text and distractors drawn from every enabled book. An approved
    /// submission whose spelling and definition were kept is asked through its
    /// catalog meaning. A rejected submission, or one approved with a changed
    /// spelling or definition, has no question: the text the client learned is no
    /// longer in the catalog.
    /// </summary>
    public async Task<VocabularyQuestionModel> CreateQuestionAsync(
        string id,
        bool chineseToEnglish,
        CancellationToken cancellationToken = default)
    {
        var submission = await _submissions.GetByIdAsync(id.Trim(), cancellationToken)
            ?? throw new ResourceNotFoundException("Meaning submission was not found.");
        if (submission.Status == MeaningSubmissionStatus.Approved && submission.DefinitionKept)
        {
            return await _vocabularyService.CreateQuestionAsync(
                submission.ResultWordId!,
                submission.ResultBookId!,
                chineseToEnglish,
                submission.ResultMeaningId);
        }

        if (submission.Status != MeaningSubmissionStatus.Pending)
        {
            throw new ResourceNotFoundException("Meaning submission was not found.");
        }

        var normalizedWord = submission.Word.Trim().ToLowerInvariant();
        var normalizedMeaning = submission.Meaning.Trim().ToLowerInvariant();
        List<VocabularyQuestionOptionModel> options;
        string stem;
        if (chineseToEnglish)
        {
            stem = submission.Meaning;
            var distractors = await _submissions.GetCatalogWordDistractorsAsync(
                normalizedWord, normalizedMeaning, 3, cancellationToken);
            options =
            [
                new VocabularyQuestionOptionModel { Text = submission.Word, IsCorrect = true },
                .. distractors.Select(item => new VocabularyQuestionOptionModel
                {
                    Text = item.Word, IsCorrect = false, WordId = item.Id
                })
            ];
        }
        else
        {
            stem = submission.Word;
            var distractors = await _submissions.GetCatalogMeaningDistractorsAsync(
                normalizedWord, normalizedMeaning, 3, cancellationToken);
            options =
            [
                new VocabularyQuestionOptionModel { Text = submission.Meaning, IsCorrect = true },
                .. distractors.Select(item => new VocabularyQuestionOptionModel
                {
                    Text = item.Meaning, IsCorrect = false, WordId = item.VocabularyId, MeaningId = item.Id
                })
            ];
        }

        options = options
            .Where(option => !string.IsNullOrWhiteSpace(option.Text))
            .DistinctBy(option => option.Text, StringComparer.Ordinal)
            .ToList();
        if (options.Count != 4 || options.Count(option => option.IsCorrect) != 1)
        {
            throw new BusinessRuleException(
                "The catalog does not contain enough distinct words to create a question.");
        }

        for (var index = options.Count - 1; index > 0; index--)
        {
            var swap = Random.Shared.Next(index + 1);
            (options[index], options[swap]) = (options[swap], options[index]);
        }

        return new VocabularyQuestionModel
        {
            Word = stem,
            WordId = string.Empty,
            MeaningId = string.Empty,
            ChineseToEnglish = chineseToEnglish,
            Options = options
        };
    }

    public Task<(List<MeaningSubmissionModel> Items, int TotalCount)> PageAsync(
        MeaningSubmissionStatus status,
        int page,
        int size,
        CancellationToken cancellationToken = default)
    {
        return _submissions.PageAsync(status, Math.Max(1, page), Math.Clamp(size, 1, 100), cancellationToken);
    }

    /// <summary>
    /// Writes the reviewed definition into the supplementary book with the same
    /// matching an import uses, and marks the submission approved, in one
    /// transaction. Phonetics given here merge into the word as an import would.
    /// </summary>
    public async Task<MeaningSubmissionModel> ApproveAsync(
        string id,
        MeaningSubmissionApproval approval,
        CancellationToken cancellationToken = default)
    {
        var bookId = RequireBook();
        var spelling = Required(approval.Word, MaxWordLength, "Word");
        var definition = Required(approval.Meaning, MaxMeaningLength, "Meaning");
        var partOfSpeech = Optional(approval.PartOfSpeech, MaxPartOfSpeechLength, "Part of speech");
        var example = Optional(approval.Example, MaxExampleLength, "Example");

        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var submission = await RequirePendingAsync(id, cancellationToken);
            var (word, meaning, _) = await _vocabularyService.AddOrUpdateCoreAsync(
                new VocabularyModel
                {
                    Word = spelling,
                    PhoneticUk = Optional(approval.PhoneticUk, 100, "UK phonetic"),
                    PhoneticUs = Optional(approval.PhoneticUs, 100, "US phonetic")
                },
                new VocabularyMeaningModel
                {
                    BookId = bookId,
                    PartOfSpeech = partOfSpeech,
                    Meaning = definition,
                    Example = example
                });
            MarkApproved(submission, word, meaning, _time.GetUtcNow());
            await _submissions.UpdateAsync(submission, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return submission;
        }, cancellationToken);
    }

    public async Task<MeaningSubmissionModel> RejectAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var submission = await RequirePendingAsync(id, cancellationToken);
            var now = _time.GetUtcNow();
            submission.Status = MeaningSubmissionStatus.Rejected;
            submission.ReviewedAt = now;
            submission.UpdatedAt = now;
            await _submissions.UpdateAsync(submission, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return submission;
        }, cancellationToken);
    }

    public async Task<MeaningSubmissionReviewAdvice> AdviseAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_assistant is not { IsEnabled: true })
        {
            throw new BusinessRuleException("The review assistant is not configured.");
        }

        var submission = await _submissions.GetByIdAsync(id.Trim(), cancellationToken)
            ?? throw new ResourceNotFoundException("Meaning submission was not found.");
        return await _assistant.ReviewAsync(submission, cancellationToken);
    }

    private async Task<MeaningSubmissionModel> RequirePendingAsync(string id, CancellationToken cancellationToken)
    {
        var submission = await _submissions.GetByIdAsync(id.Trim(), cancellationToken)
            ?? throw new ResourceNotFoundException("Meaning submission was not found.");
        if (submission.Status != MeaningSubmissionStatus.Pending)
        {
            throw new ConflictException("Meaning submission has already been reviewed.");
        }

        return submission;
    }

    private static void MarkApproved(
        MeaningSubmissionModel submission,
        VocabularyModel word,
        VocabularyMeaningModel meaning,
        DateTimeOffset now)
    {
        submission.Status = MeaningSubmissionStatus.Approved;
        submission.ResultBookId = meaning.BookId;
        submission.ResultWordId = word.Id;
        submission.ResultMeaningId = meaning.Id;
        submission.ResultWord = word.Word;
        submission.ResultMeaning = meaning.Meaning;
        submission.ResultPartOfSpeech = string.IsNullOrEmpty(meaning.PartOfSpeech) ? null : meaning.PartOfSpeech;
        submission.ReviewedAt = now;
        submission.UpdatedAt = now;
    }

    private string RequireBook()
    {
        if (!_settings.IsConfigured)
        {
            throw new MeaningSubmissionsNotConfiguredException();
        }

        return _settings.BookId!.Trim();
    }

    private static string Required(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException($"{name} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException($"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException($"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }
}

/// <summary>The deployment names no supplementary book, so submissions are off.</summary>
public sealed class MeaningSubmissionsNotConfiguredException : Exception
{
    public MeaningSubmissionsNotConfiguredException()
        : base("Meaning submissions are not configured.")
    {
    }
}
