using Microsoft.Extensions.Options;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyLearningOptionsValidation : IValidateOptions<VocabularyLearningOptions>
{
    public ValidateOptionsResult Validate(string? name, VocabularyLearningOptions options)
    {
        var failures = VocabularyLearningOptions.Validate(options).ToList();
        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
