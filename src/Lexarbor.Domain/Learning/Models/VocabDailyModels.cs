namespace Lexarbor.Learning.Domain.Models;

public enum VocabQuestionKind
{
    New = 1,
    Review = 2,
    Extra = 3,
}

/// <summary>Question direction; a card first appears as English to Chinese.</summary>
public enum VocabQuestionDirection
{

    EnglishToChinese = 1,

    ChineseToEnglish = 2,

    /// <summary>Recall hidden letters from a persisted word pattern.</summary>
    FillLetters = 3,

    FormChoice = 4,
    RelationChoice = 5,
    FormFill = 6,
    PartOfSpeechChoice = 7,
}

public static class VocabQuestionDirections
{
    public static bool IsSpelling(this VocabQuestionDirection direction) =>
        direction is VocabQuestionDirection.FillLetters or VocabQuestionDirection.FormFill;

    public static bool IsLinked(this VocabQuestionDirection direction) =>
        direction is VocabQuestionDirection.FormChoice or VocabQuestionDirection.RelationChoice or VocabQuestionDirection.FormFill;

    public static bool ShowsTargetWord(this VocabQuestionDirection direction) =>
        direction is VocabQuestionDirection.EnglishToChinese or VocabQuestionDirection.RelationChoice;
}

public enum VocabAttemptResult
{
    Correct = 1,
    Wrong = 2,
    Unknown = 3,
}
public sealed class VocabPlanDayModel
{
    public Guid PlanId { get; set; }

    public DateOnly LocalDate { get; set; }

    public int NewWordsTarget { get; set; }

    public int NewWordsLearned { get; set; }

    public VocabDailyTasks? Tasks { get; set; }

    /// <summary>Meaning keys assigned to the independent, word-limited review group.</summary>
    public List<CardKey> DueInScope { get; set; } = [];

    public List<CardKey> ReviewedInScope { get; set; } = [];

    public DateTimeOffset? CompletedAt { get; set; }

    public bool NewWordsExhausted { get; set; }

    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record VocabQuestionOption(Guid OptionId, string Text);
public sealed class VocabIssuedQuestionModel
{
    public Guid Id { get; set; }

    public Guid StudentId { get; set; }

    public Guid? PlanId { get; set; }

    public VocabQuestionKind Kind { get; set; }

    public string WordKey { get; set; } = string.Empty;

    public string MeaningKey { get; set; } = string.Empty;

    public string WordId { get; set; } = string.Empty;

    public string MeaningId { get; set; } = string.Empty;

    public string BookId { get; set; } = string.Empty;

    public string? UnitId { get; set; }

    public VocabQuestionDirection Direction { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string? AnchorWord { get; set; }

    public string? PartOfSpeech { get; set; }
    public string? RelationType { get; set; }
    public VocabFamilyHint? FamilySource { get; set; }

    public List<VocabQuestionOption> Options { get; set; } = [];

    public Guid? CorrectOptionId { get; set; }

    /// <summary>Visible characters and null blanks; persisted only, never an answer DTO.</summary>
    public List<string?>? LetterPattern { get; set; }

    /// <summary>Exact answer word, revealed only after saved judgement.</summary>
    public string? AnswerWord { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }
}

public sealed record VocabFamilyHint(string Word, string RelationType, string Direction);

public sealed class VocabAttemptModel
{
    public Guid IssuedQuestionId { get; set; }

    public VocabAttemptResult Result { get; set; }

    public Guid? SelectedOptionId { get; set; }

    /// <summary>Original submitted letters, preserving case.</summary>
    public List<string>? FilledLetters { get; set; }

    public int ElapsedMs { get; set; }

    public ReviewRating Rating { get; set; }

    public DateTimeOffset AnsweredAt { get; set; }

    public DateOnly LocalDate { get; set; }
}

public sealed record VocabTodaySnapshot(
    Guid PlanId,
    DateOnly LocalDate,
    int NewWordsTarget,
    int NewWordsLearned,
    int ReviewRequired,
    int ReviewCompleted,
    int AllDueCount,
    DateTimeOffset? CompletedAt,
    bool CanLearnExtra,
    DateTimeOffset? TeacherAdjustedAt,
    bool NewWordsExhausted = false,
    int? LearningRequired = null,
    int? LearningCompleted = null,
    int FillReviewRequired = 0,
    int FillReviewCompleted = 0,
    int? ExtraBatchSize = null,
    VocabExtraBatchSnapshot? ExtraBatch = null);

public sealed record VocabNextQuestionOutcome(
    VocabIssuedQuestionModel? Question,
    string? DoneReason,
    VocabExtraBatchSnapshot? ExtraBatch = null)
{
    public static VocabNextQuestionOutcome Next(VocabIssuedQuestionModel question, VocabExtraBatchSnapshot? extraBatch = null) => new(question, null, extraBatch);

    public static VocabNextQuestionOutcome Done(string reason, VocabExtraBatchSnapshot? extraBatch = null) => new(null, reason, extraBatch);
}

public sealed record VocabAnswerResult(
    Guid QuestionId,
    VocabAttemptResult Result,
    Guid? CorrectOptionId,
    ReviewRating Rating,
    DateTimeOffset DueAt,
    string? CorrectWord = null);

public sealed record VocabFreeReviewSnapshot(Guid StudentId, DateOnly LocalDate, int AllDue);
