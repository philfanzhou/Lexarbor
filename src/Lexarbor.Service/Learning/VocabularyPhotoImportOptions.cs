using Microsoft.Extensions.Configuration;

namespace Lexarbor.Learning.Service;

/// <summary>
/// Photo import settings. When enabled, the StructaDoc address and scoped <c>image:recognize</c> key are required.
/// </summary>
public sealed class VocabularyPhotoImportOptions
{
    public const string SectionName = "VocabularyPhotoImport";
    public const int DefaultRecognitionBudgetSeconds = 150;
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;

    public bool Enabled { get; private init; }

    /// <summary>StructaDoc's service root; the client appends <c>api/v1/image/recognize</c>.</summary>
    public string BaseUrl { get; private init; } = string.Empty;

    /// <summary>Presented as <c>Authorization: ApiKey &lt;key&gt;</c>; never logged.</summary>
    public string ApiKey { get; private init; } = string.Empty;

    /// <summary>The whole StructaDoc exchange, above its own 120-second cloud deadline.</summary>
    public int RecognitionBudgetSeconds { get; private init; } = DefaultRecognitionBudgetSeconds;

    public int MaxResponseBytes { get; private init; } = DefaultMaxResponseBytes;

    public static VocabularyPhotoImportOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var enabledText = section["Enabled"];
        if (string.IsNullOrWhiteSpace(enabledText)) return new VocabularyPhotoImportOptions();
        if (!bool.TryParse(enabledText, out var enabled)) throw Invalid("Enabled", "must be true or false");
        if (!enabled) return new VocabularyPhotoImportOptions();

        var baseUrl = (section["BaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw Invalid("BaseUrl", "must be an absolute HTTP(S) URL without credentials, query or fragment");
        var key = (section["ApiKey"] ?? string.Empty).Trim();
        if (key.Length == 0) throw Invalid("ApiKey", "is required");
        var budget = Integer(section, "RecognitionBudgetSeconds", DefaultRecognitionBudgetSeconds, 1, 3600);
        var maxBytes = Integer(section, "MaxResponseBytes", DefaultMaxResponseBytes, 4096, 64 * 1024 * 1024);
        return new VocabularyPhotoImportOptions
        {
            Enabled = true,
            BaseUrl = baseUrl,
            ApiKey = key,
            RecognitionBudgetSeconds = budget,
            MaxResponseBytes = maxBytes,
        };
    }

    private static int Integer(IConfiguration section, string name, int fallback, int min, int max)
    {
        var text = section[name];
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        return int.TryParse(text, out var value) && value >= min && value <= max
            ? value
            : throw Invalid(name, $"must be an integer from {min} to {max}");
    }

    private static InvalidOperationException Invalid(string name, string rule) => new($"{SectionName}:{name} {rule}.");
}
