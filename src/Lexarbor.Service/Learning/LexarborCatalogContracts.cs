namespace Lexarbor.Learning.Service;

public static class VocabularyCatalogErrorCodes
{
    public const string BookNotFound = "VOCAB_BOOK_NOT_FOUND";
    public const string BookDisabled = "VOCAB_BOOK_DISABLED";
    public const string UnitNotFound = "VOCAB_UNIT_NOT_FOUND";
    public const string AudioUnavailable = "VOCAB_AUDIO_UNAVAILABLE";
    public const string CatalogUnavailable = "VOCAB_CATALOG_UNAVAILABLE";
}
public sealed class LexarborCatalogException : Exception
{
    public string ErrorCode { get; }

    public bool Retryable { get; }

    public LexarborCatalogException(string errorCode, bool retryable, string message) : base(message)
    {
        ErrorCode = errorCode;
        Retryable = retryable;
    }
}

public sealed class LexarborOptions
{
    public const string SectionName = "Lexarbor";

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public bool AllowInsecureHttp { get; set; }
    public string SupplementaryBookId { get; set; } = string.Empty;
}
