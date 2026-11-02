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
    public const int MaxReviewCommentLength = 500;
    public const int MaxReviewModelLength = 200;

    private readonly IMeaningSubmissionRepository _submissions;
    private readonly IVocabularyRepository _vocabularyRepository;
    private readonly IVocabularyMeaningRepository _meaningRepository;
    private readonly VocabularyDomainService _vocabularyService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly MeaningSubmissionSettings _settings;
    private readonly IMeaningSubmissionReviewAssistant? _assistant;
    private readonly TimeProvider _time;
    private readonly IMeaningSubmissionChangeNotifier? _changes;
    private readonly IMeaningSubmissionAutomaticReviewStatusProvider? _automaticReview;

    public MeaningSubmissionService(
        IMeaningSubmissionRepository submissions,
        IVocabularyRepository vocabularyRepository,
        IVocabularyMeaningRepository meaningRepository,
        VocabularyDomainService vocabularyService,
        IUnitOfWork unitOfWork,
        MeaningSubmissionSettings settings,
        TimeProvider time,
        IMeaningSubmissionReviewAssistant? assistant = null,
        IMeaningSubmissionChangeNotifier? changes = null,
        IMeaningSubmissionAutomaticReviewStatusProvider? automaticReview = null)
    {
        _submissions = submissions;
        _vocabularyRepository = vocabularyRepository;
        _meaningRepository = meaningRepository;
        _vocabularyService = vocabularyService;
        _unitOfWork = unitOfWork;
        _settings = settings;
        _time = time;
        _assistant = assistant;
        _changes = changes;
        _automaticReview = automaticReview;
    }

    public bool IsConfigured => _settings.IsConfigured;

    public string? BookId => _settings.IsConfigured ? _settings.BookId!.Trim() : null;

    public bool ReviewAssistantEnabled => _assistant?.IsEnabled == true;
    public Task<MeaningSubmissionAutomaticReviewStatus?> AutomaticReviewStatusAsync(CancellationToken token) =>
        _automaticReview == null ? Task.FromResult<MeaningSubmissionAutomaticReviewStatus?>(null) : ReadAutomaticReviewAsync(token);
    private async Task<MeaningSubmissionAutomaticReviewStatus?> ReadAutomaticReviewAsync(CancellationToken token) => await _automaticReview!.ReadAsync(token);

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

        var result = await _unitOfWork.ExecuteInTransactionAsync(async () =>
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
        if (result.Status == MeaningSubmissionStatus.Pending)
            try { _changes?.Notify(); } catch (Exception) { /* A lossy wakeup cannot undo a successful commit. */ }
        return result;
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
        CancellationToken cancellationToken = default,
        string? reviewerKind = null)
    {
        return _submissions.PageAsync(status, Math.Max(1, page), Math.Clamp(size, 1, 100), cancellationToken, reviewerKind);
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
        var normalized = ValidateApproval(approval);
        var comment = Optional(approval.ReviewComment, MaxReviewCommentLength, "Review comment");
        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var submission = await RequirePendingAsync(id, cancellationToken);
            await WriteApprovalAsync(submission, normalized, cancellationToken);
            submission.ReviewerKind = "admin";
            submission.ReviewComment = submission.DefinitionKept ? null : comment;
            await _submissions.UpdateAsync(submission, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return submission;
        }, cancellationToken);
    }

    public Task<MeaningSubmissionModel> RejectAsync(string id, CancellationToken cancellationToken = default) =>
        RejectAsync(id, null, cancellationToken);

    public async Task<MeaningSubmissionModel> RejectAsync(string id, string? reviewComment, CancellationToken cancellationToken = default)
    {
        var comment = Optional(reviewComment, MaxReviewCommentLength, "Review comment");
        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var submission = await RequirePendingAsync(id, cancellationToken);
            MarkRejected(submission, _time.GetUtcNow());
            submission.ReviewerKind = "admin";
            submission.ReviewComment = comment;
            await _submissions.UpdateAsync(submission, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return submission;
        }, cancellationToken);
    }

    /// <summary>
    /// Records an already validated model conclusion. No network IO occurs here.
    /// The eligibility port executes under this same writer, after reading the
    /// current pending state. Replays return the original application fact.
    /// </summary>
    public async Task<MeaningSubmissionAiReviewRecord> ApplyAiReviewAsync(
        MeaningSubmissionAiReview review,
        IMeaningSubmissionReviewEligibility eligibility,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        review = ValidateReview(review);
        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var previous = await _submissions.GetReviewByAttemptIdAsync(review.AttemptId, cancellationToken);
            if (previous != null)
            {
                if (previous.Review != review)
                    throw new ConflictException("AI review attempt already has a different conclusion.");
                return previous;
            }
            var submission = await _submissions.GetByIdAsync(review.SubmissionId, cancellationToken)
                ?? throw new ResourceNotFoundException("Meaning submission was not found.");
            var applied = submission.Status == MeaningSubmissionStatus.Pending
                && await eligibility.CanApplyAsync(submission.Id, review.AttemptId, cancellationToken);
            if (applied)
            {
                if (review.Verdict == "reject")
                    MarkRejected(submission, review.ReviewedAt);
                else
                {
                    var accept = review.Verdict == "accept";
                    var uk = review.PhoneticUk;
                    var us = review.PhoneticUs;
                    var matched = await _vocabularyRepository.GetByNormalizedWordAsync(submission.Word.ToLowerInvariant());
                    var current = matched == null ? null : await _vocabularyRepository.GetByIdAsync(matched.Id);
                    if (accept)
                    {
                        // This read and catalog merge share the writer: a default
                        // edited while the AI was running always wins over accept.
                        if (!string.IsNullOrWhiteSpace(current?.PhoneticUk)) uk = null;
                        if (!string.IsNullOrWhiteSpace(current?.PhoneticUs)) us = null;
                    }
                    await WriteApprovalAsync(submission, new(submission.Word,
                        accept ? submission.PartOfSpeech : review.PartOfSpeech,
                        accept ? submission.Meaning : review.Meaning!, submission.Example, uk, us), cancellationToken, current?.Id);
                    submission.ReviewedAt = review.ReviewedAt;
                    submission.UpdatedAt = review.ReviewedAt;
                }
                submission.AiReviewVerdict = review.Verdict;
                submission.ReviewerKind = "ai";
                submission.ReviewComment = review.Verdict == "accept" ? null : review.Comment;
                await _submissions.UpdateAsync(submission, cancellationToken);
            }
            var record = new MeaningSubmissionAiReviewRecord(Guid.NewGuid().ToString(), review, applied);
            await _submissions.AddReviewAsync(record, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return record;
        }, cancellationToken);
    }

    private async Task WriteApprovalAsync(MeaningSubmissionModel submission, MeaningSubmissionApproval approval, CancellationToken cancellationToken, string? existingWordId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (word, meaning, _) = await _vocabularyService.AddOrUpdateCoreAsync(
            new VocabularyModel { Id = existingWordId ?? string.Empty, Word = approval.Word, PhoneticUk = approval.PhoneticUk, PhoneticUs = approval.PhoneticUs },
            new VocabularyMeaningModel { BookId = RequireBook(), PartOfSpeech = approval.PartOfSpeech, Meaning = approval.Meaning, Example = approval.Example });
        cancellationToken.ThrowIfCancellationRequested();
        MarkApproved(submission, word, meaning, _time.GetUtcNow());
    }

    private static MeaningSubmissionApproval ValidateApproval(MeaningSubmissionApproval approval) => new(
        Required(approval.Word, MaxWordLength, "Word"),
        Optional(approval.PartOfSpeech, MaxPartOfSpeechLength, "Part of speech"),
        Required(approval.Meaning, MaxMeaningLength, "Meaning"),
        Optional(approval.Example, MaxExampleLength, "Example"),
        Optional(approval.PhoneticUk, 100, "UK phonetic"),
        Optional(approval.PhoneticUs, 100, "US phonetic"));

    private static MeaningSubmissionAiReview ValidateReview(MeaningSubmissionAiReview review)
    {
        if (review.Verdict is not ("accept" or "revise" or "reject"))
            throw new DomainValidationException("AI review verdict is invalid.");
        return review with
        {
            AttemptId = Required(review.AttemptId, 100, "Attempt ID"),
            SubmissionId = Required(review.SubmissionId, 100, "Submission ID"),
            PartOfSpeech = Optional(review.PartOfSpeech, MaxPartOfSpeechLength, "Part of speech"),
            Meaning = review.Verdict == "revise" ? Required(review.Meaning, MaxMeaningLength, "Meaning") : Optional(review.Meaning, MaxMeaningLength, "Meaning"),
            PhoneticUk = Optional(review.PhoneticUk, 100, "UK phonetic"),
            PhoneticUs = Optional(review.PhoneticUs, 100, "US phonetic"),
            Comment = review.Verdict == "accept" ? Optional(review.Comment, MaxReviewCommentLength, "Review comment") : Required(review.Comment, MaxReviewCommentLength, "Review comment"),
            Model = Required(review.Model, MaxReviewModelLength, "Model"),
            ReviewedAt = DateTimeOffset.FromUnixTimeMilliseconds(review.ReviewedAt.ToUnixTimeMilliseconds())
        };
    }

    private static void MarkRejected(MeaningSubmissionModel submission, DateTimeOffset now)
    {
        submission.Status = MeaningSubmissionStatus.Rejected;
        submission.ReviewedAt = now;
        submission.UpdatedAt = now;
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
