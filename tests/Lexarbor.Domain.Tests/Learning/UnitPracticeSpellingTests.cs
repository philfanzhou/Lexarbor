using Lexarbor.Learning.Domain.Models;
using Xunit;

namespace Lexarbor.Domain.Tests.Learning;

public sealed class UnitPracticeSpellingTests
{
    [Fact]
    public void SharedPromptsIdentifyTheIntendedSpellingAndUniquePromptsStayHidden()
    {
        var hints = UnitPracticeSpelling.Hints([
            new("large", "1", "large", "大", "adjective"),
            new("great", "2", "great", "大", "adjective"),
            new("big", "3", "big", "大", "adjective"),
            new("pear", "4", "pear", "梨", "noun")
        ]);

        Assert.Equal("l____", hints["1"]);
        Assert.Equal("g____", hints["2"]);
        Assert.Equal("___", hints["3"]);
        Assert.DoesNotContain("4", hints.Keys);
    }
}
