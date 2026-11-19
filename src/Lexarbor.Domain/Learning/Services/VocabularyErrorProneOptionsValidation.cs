using Lexarbor.Learning.Domain.Models;
using Microsoft.Extensions.Options;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyErrorProneOptionsValidation : IValidateOptions<VocabularyErrorProneOptions>
{
    public ValidateOptionsResult Validate(string? name, VocabularyErrorProneOptions options)
    {
        var errors = new List<string>();
        if (options.WindowDays is < 1 or > 3650) errors.Add("VocabularyLearning:ErrorProne:WindowDays");
        if (options.MinimumAttempts < 1) errors.Add("VocabularyLearning:ErrorProne:MinimumAttempts");
        if (!double.IsFinite(options.ErrorRateThreshold) || options.ErrorRateThreshold is < 0 or > 1)
            errors.Add("VocabularyLearning:ErrorProne:ErrorRateThreshold");
        if (options.MinimumForgetCount < 1) errors.Add("VocabularyLearning:ErrorProne:MinimumForgetCount");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
