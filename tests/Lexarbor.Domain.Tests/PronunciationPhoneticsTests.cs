using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class PronunciationPhoneticsTests
{
    [Theory]
    [InlineData("[ ˈsteɪb(ə)l ]", "ˈsteɪbl", true)]
    [InlineData(" /liːd/ ; [led] ", "/led/", true)]
    [InlineData("/liːd; led/", "led", true)]
    [InlineData("ˈsteɪb(ə)l", "ˈsteɪbɪl", false)]
    [InlineData("liːd;led", "liːd led", false)]
    [InlineData("liːd;", "liːd", false)]
    [InlineData("liːd()", "liːd", false)]
    [InlineData("liːd((ə))", "liːdə", false)]
    [InlineData("liːd(ə", "liːdə", false)]
    [InlineData("liːd)", "liːd", false)]
    [InlineData("[[liːd]]", "liːd", false)]
    [InlineData("liːd( or led)", "liːd", false)]
    [InlineData("liːd(ə)(ə)(ə)(ə)(ə)(ə)(ə)", "liːd", false)]
    [InlineData("ˈkɪl.ə.græmz", "ˈkɪləɡræmz", true)]
    [InlineData("ˌiː  ˈjuː", "ˌiː ˈjuː", true)]
    [InlineData("ˈrecord", "reˈcord", false)]
    [InlineData("ˈænti", "ˈæntaɪ", false)]
    public void DictionaryNotationIsBoundedAndNeverInventsAnotherReading(string notation, string reading, bool allowed)
        => Assert.Equal(allowed, PronunciationPhonetics.Allows(notation, reading));
}
