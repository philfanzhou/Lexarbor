namespace Lexarbor.Domain.Models;

/// <summary>Server-selected upload policy; never inferred from an object location or client input.</summary>
public enum AudioUploadPurpose
{
    Vocabulary,
    Textbook
}
