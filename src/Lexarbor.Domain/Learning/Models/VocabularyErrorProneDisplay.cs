namespace Lexarbor.Learning.Domain.Models;

// Private persisted question projection, used only to build explicit statistics display fields.
public sealed record VocabularyErrorProneDisplay(int Direction, string Prompt, string? OptionsSnapshot, Guid? CorrectOptionId,
    string? AnswerWord, string? WordId = null, string? MeaningId = null, string? BookId = null);
