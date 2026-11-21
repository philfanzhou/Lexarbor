using Lexarbor.Learning.Domain.Models;
using Xunit;

namespace Lexarbor.Domain.Tests.Learning;

public sealed class UnitPracticeGradingTests
{
    private static readonly Guid Right = Guid.NewGuid(), Wrong = Guid.NewGuid();
    private static readonly UnitPracticeIssued Choice = new("ChineseToEnglish", "苹果",
        [new(Wrong, "pear", false), new(Right, "apple", true)]);
    private static readonly UnitPracticeIssued Fill = new("FillLetters", "苹果", Pattern: ["a", null, "p", null, "e"], AnswerWord: "apple");

    [Fact]
    public void EachTypeTakesItsOwnAnswerKind()
    {
        Assert.True(UnitPracticeGrading.Judge(null, "apple", new(" APPLE ", null, null, false)));
        Assert.False(UnitPracticeGrading.Judge(null, "apple", new("pear", null, null, false)));
        Assert.True(UnitPracticeGrading.Judge(Choice, "apple", new(null, Right, null, false)));
        Assert.False(UnitPracticeGrading.Judge(Choice, "apple", new(null, Wrong, null, false)));
        Assert.True(UnitPracticeGrading.Judge(Fill, "apple", new(null, null, ["P", "l"], false)));
        Assert.False(UnitPracticeGrading.Judge(Fill, "apple", new(null, null, ["p", "p"], false)));
        foreach (var issued in new[] { null, Choice, Fill })
            Assert.False(UnitPracticeGrading.Judge(issued, "apple", new(null, null, null, true)));
    }

    [Fact]
    public void MismatchedOrMalformedAnswersAreRejected()
    {
        Assert.Null(UnitPracticeGrading.Judge(null, "apple", new(null, Right, null, false)));
        Assert.Null(UnitPracticeGrading.Judge(null, "apple", new("apple", null, null, true)));
        Assert.Null(UnitPracticeGrading.Judge(Choice, "apple", new("apple", null, null, false)));
        Assert.Null(UnitPracticeGrading.Judge(Choice, "apple", new(null, Guid.NewGuid(), null, false)));
        Assert.Null(UnitPracticeGrading.Judge(Fill, "apple", new("apple", null, null, false)));
        Assert.Null(UnitPracticeGrading.Judge(Fill, "apple", new(null, null, ["p"], false)));
        Assert.Null(UnitPracticeGrading.Judge(Fill, "apple", new(null, null, ["p", "1"], false)));
        Assert.Null(UnitPracticeGrading.Judge(Fill, "apple", new(null, null, ["p", "ll"], false)));
    }

    [Fact]
    public void ReissueKeepsTheQuestionWithFreshOptionIds()
    {
        var again = Choice.Reissue(max => 0);
        Assert.Equal(Choice.Options!.Select(o => (o.Text, o.IsCorrect)).Order(), again.Options!.Select(o => (o.Text, o.IsCorrect)).Order());
        Assert.Empty(again.Options!.Select(o => o.OptionId).Intersect(Choice.Options!.Select(o => o.OptionId)));
        Assert.Same(Fill, Fill.Reissue(max => 0));
    }
}
