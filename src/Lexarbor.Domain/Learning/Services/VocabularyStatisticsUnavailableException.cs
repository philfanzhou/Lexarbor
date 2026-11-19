namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyStatisticsUnavailableException() : Exception("Vocabulary statistics changed during the read.");
