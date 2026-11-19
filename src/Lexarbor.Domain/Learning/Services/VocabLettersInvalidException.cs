namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabLettersInvalidException : Exception
{
    public VocabLettersInvalidException() : base("Provide exactly one Unicode letter for each blank, or unknown alone.") { }
}
