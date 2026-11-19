using Microsoft.Extensions.Options;

namespace Lexarbor.Learning.Domain.Services;
public sealed class VocabularyFsrsOptionsValidation : IValidateOptions<VocabularyFsrsOptions>
{
    public ValidateOptionsResult Validate(string? name, VocabularyFsrsOptions options)
    {
        var failures = VocabularyFsrsOptions.Validate(options).ToList();
        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
