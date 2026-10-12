namespace Lexarbor.Service.Dtos;

public sealed record AudioBindingDto(string AssetId, string SourceRevision);
public sealed record AudioGenerationInputDto(string Word, string? Phonetic, string? PartOfSpeech, string? Meaning);
public sealed record AudioTargetDto(string WordId, string? MeaningId, string Locale, string TargetType,
    string Status, string? Source, string SourceRevision, AudioGenerationInputDto Input,
    AudioBindingDto? OwnBinding, AudioBindingDto? EffectiveBinding);
