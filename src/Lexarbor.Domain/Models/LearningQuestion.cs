namespace Lexarbor.Domain.Models;

public enum LearningQuestionMode { New, Review }
public enum LearningQuestionType { EnglishToChinese, ChineseToEnglish, FillLetters }

public abstract record LearningQuestion(LearningQuestionType Type, string Prompt);

public sealed record ChoiceLearningQuestion(
    LearningQuestionType QuestionType, string QuestionPrompt, IReadOnlyList<VocabularyQuestionOptionModel> Options)
    : LearningQuestion(QuestionType, QuestionPrompt);

public sealed record FillLettersLearningQuestion(string QuestionPrompt, IReadOnlyList<string?> Pattern, string AnswerWord)
    : LearningQuestion(LearningQuestionType.FillLetters, QuestionPrompt);
