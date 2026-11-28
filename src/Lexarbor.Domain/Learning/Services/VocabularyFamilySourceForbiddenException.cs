namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyFamilySourceForbiddenException : Exception
{
    public VocabularyFamilySourceForbiddenException() : base("This word is not eligible for family expansion.") { }
}
