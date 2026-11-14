namespace Lexarbor.Domain.Models;

public enum LearningQuestionMode { New, Review }
public enum LearningQuestionType { EnglishToChinese, ChineseToEnglish, FillLetters, FormChoice, RelationChoice, FormFill, PartOfSpeechChoice }

public sealed record LearningQuestionRelation(string SourceWordId, string Type);

public sealed record LearningQuestionRelationContext(string AnchorWord, IReadOnlyList<string> Types, IReadOnlyList<string> FormDistractors);

public abstract record LearningQuestion(LearningQuestionType Type, string Prompt);

public sealed record ChoiceLearningQuestion(
    LearningQuestionType QuestionType, string QuestionPrompt, IReadOnlyList<VocabularyQuestionOptionModel> Options)
    : LearningQuestion(QuestionType, QuestionPrompt);

public sealed record FillLettersLearningQuestion(string QuestionPrompt, IReadOnlyList<string?> Pattern, string AnswerWord)
    : LearningQuestion(LearningQuestionType.FillLetters, QuestionPrompt);

public sealed record FormChoiceLearningQuestion(string AnchorWord, string RelationType, IReadOnlyList<VocabularyQuestionOptionModel> Options)
    : LearningQuestion(LearningQuestionType.FormChoice, AnchorWord);

public sealed record RelationChoiceLearningQuestion(string TargetWord, string AnchorWord, IReadOnlyList<VocabularyQuestionOptionModel> Options)
    : LearningQuestion(LearningQuestionType.RelationChoice, TargetWord);

public sealed record FormFillLearningQuestion(string Meaning, string AnchorWord, string RelationType, IReadOnlyList<string?> Pattern, string AnswerWord)
    : LearningQuestion(LearningQuestionType.FormFill, Meaning);

public sealed record PartOfSpeechChoiceLearningQuestion(string Meaning, string AnchorWord, string PartOfSpeech, IReadOnlyList<VocabularyQuestionOptionModel> Options)
    : LearningQuestion(LearningQuestionType.PartOfSpeechChoice, Meaning);

public sealed record PartOfSpeechChoiceCandidates(string AnchorWord, IReadOnlyList<string> Distractors);
