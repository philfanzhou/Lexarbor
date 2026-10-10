using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// The request-scoped narrowing of question generation: naming the meaning to
/// ask about, drawing distractors from one unit, and filtering them by entry
/// kind. The unpinned behaviour — random meaning, whole-book distractors — is
/// covered by <see cref="VocabularyQuestionTests"/> and must not change.
/// </summary>
public class VocabularyQuestionScopeTests : TestBase
{
    private readonly VocabularyDomainService _service;

    public VocabularyQuestionScopeTests()
    {
        _service = new VocabularyDomainService(
            _vocabularyRepository,
            _bookRepository,
            _meaningRepository,
            _bookUnitRepository,
            _meaningUnitRepository,
            _unitOfWork);
    }

    // ==================== meaningId ====================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_WithMeaningId_AsksThatMeaning(bool chineseToEnglish)
    {
        var bank = await SeedMultiSenseBookAsync();

        var question = await _service.CreateQuestionAsync(
            bank.WordId,
            bank.BookId,
            chineseToEnglish,
            meaningId: bank.VerbMeaningId);

        // Naming the sense pins the stem and the correct option to it in both
        // directions; nothing is drawn.
        Assert.Equal(bank.WordId, question.WordId);
        Assert.Equal(bank.VerbMeaningId, question.MeaningId);
        Assert.Equal(chineseToEnglish, question.ChineseToEnglish);
        var correct = Assert.Single(question.Options, option => option.IsCorrect);
        if (chineseToEnglish)
        {
            Assert.Equal("to save", question.Word);
            Assert.Equal("bank", correct.Text);
        }
        else
        {
            Assert.Equal("bank", question.Word);
            Assert.Equal("to save", correct.Text);
        }

        Assert.Equal(bank.WordId, correct.WordId);
        Assert.Equal(bank.VerbMeaningId, correct.MeaningId);
    }

    [Fact]
    public async Task CreateQuestionAsync_WithMeaningId_OfAnotherWordsMeaning_ThrowsNotFound()
    {
        var book = await CreateBookAsync();
        var apple = await SeedWordAsync(book.Id, "apple", "fruit");
        var banana = await SeedWordAsync(book.Id, "banana", "yellow fruit");

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _service.CreateQuestionAsync(
                apple.Word.Id, book.Id, chineseToEnglish: true, meaningId: banana.Meaning.Id));

        Assert.Equal(
            "Vocabulary meaning was not found in the requested book.",
            exception.Message);
    }

    [Fact]
    public async Task CreateQuestionAsync_WithMeaningId_OfTheSameWordsOtherBookMeaning_ThrowsNotFound()
    {
        var firstBook = await CreateBookAsync();
        var secondBook = await CreateBookAsync();
        // The word carries a meaning in each book, so the word and book checks
        // both pass and only the meaning lookup can reject the id.
        var first = await SeedWordAsync(firstBook.Id, "apple", "fruit");
        await SeedWordAsync(secondBook.Id, "apple", "pome fruit");

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _service.CreateQuestionAsync(
                first.Word.Id, secondBook.Id, chineseToEnglish: false, meaningId: first.Meaning.Id));

        Assert.Equal(
            "Vocabulary meaning was not found in the requested book.",
            exception.Message);
    }

    // ==================== unitId ====================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_WithUnitId_DistractorsComeOnlyFromThatUnit(
        bool chineseToEnglish)
    {
        var data = await SeedUnitBookAsync();
        // The asked meaning itself sits in no unit, which the contract allows:
        // only the distractors are narrowed. The unit holds exactly three
        // usable candidates, so every draw is all of them — words in one
        // direction, their meanings in the other.
        var unitWordTexts = new[]
        {
            data.UnitWords["banana"].Word.Word,
            data.UnitWords["cherry"].Word.Word,
            data.UnitWords["date"].Word.Word
        };
        var unitMeaningTexts = new[]
        {
            data.UnitWords["banana"].Meaning.Meaning,
            data.UnitWords["cherry"].Meaning.Meaning,
            data.UnitWords["date"].Meaning.Meaning
        };

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var question = await _service.CreateQuestionAsync(
                data.CorrectWord.Id,
                data.Book.Id,
                chineseToEnglish,
                unitId: data.Unit.Id);

            Assert.Equal(4, question.Options.Count);
            Assert.Single(question.Options, option => option.IsCorrect);
            var drawn = question.Options
                .Where(option => !option.IsCorrect)
                .Select(option => option.Text)
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();
            var expected = (chineseToEnglish ? unitWordTexts : unitMeaningTexts)
                .OrderBy(text => text, StringComparer.Ordinal);
            Assert.Equal(expected, drawn);
            Assert.Equal(data.CorrectWord.Id, question.WordId);

            // Word-level distractors name their word and no meaning;
            // meaning-level ones name the meaning and the word behind it.
            foreach (var option in question.Options.Where(option => !option.IsCorrect))
            {
                Assert.False(string.IsNullOrWhiteSpace(option.WordId));
                if (chineseToEnglish)
                {
                    Assert.Null(option.MeaningId);
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(option.MeaningId));
                }
            }
        }
    }

    [Fact]
    public async Task CreateQuestionAsync_UnitWithTooFewCandidates_ThrowsBusinessRule()
    {
        var data = await SeedUnitBookAsync(unitWordCount: 2);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            _service.CreateQuestionAsync(
                data.CorrectWord.Id, data.Book.Id, chineseToEnglish: true, unitId: data.Unit.Id));

        // Refusing rather than falling back to the book is the contract: the
        // caller decides whether to retry without the unit.
        Assert.Equal(
            "The vocabulary book does not contain enough distinct words to create a question.",
            exception.Message);
    }

    [Fact]
    public async Task CreateQuestionAsync_WithMissingUnitId_ThrowsNotFound()
    {
        var data = await SeedUnitBookAsync();

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _service.CreateQuestionAsync(
                data.CorrectWord.Id, data.Book.Id, chineseToEnglish: true,
                unitId: $"missing-{Guid.NewGuid():N}"));

        Assert.Equal(
            "Unit was not found in the requested vocabulary book.",
            exception.Message);
    }

    [Fact]
    public async Task CreateQuestionAsync_WithUnitOfAnotherBook_ThrowsNotFound()
    {
        var data = await SeedUnitBookAsync();
        var otherBook = await CreateBookAsync();
        var otherUnit = await CreateUnitAsync(otherBook.Id);

        // The same answer a missing unit gets, so the id of another book's unit
        // reveals nothing about that book.
        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _service.CreateQuestionAsync(
                data.CorrectWord.Id, data.Book.Id, chineseToEnglish: true, unitId: otherUnit.Id));

        Assert.Equal(
            "Unit was not found in the requested vocabulary book.",
            exception.Message);
    }

    // ==================== sameEntryKind ====================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_SameEntryKind_PhraseTarget_DrawsOnlyPhrases(
        bool chineseToEnglish)
    {
        var data = await SeedKindBookAsync();

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var question = await _service.CreateQuestionAsync(
                data.PhraseTarget.Word.Id,
                data.Book.Id,
                chineseToEnglish,
                unitId: data.Unit.Id,
                sameEntryKind: true);

            Assert.Equal(4, question.Options.Count);
            Assert.Single(question.Options, option => option.IsCorrect);
            // The unit holds exactly three phrase candidates, so every draw is
            // all of them; the words of the unit never appear.
            var drawn = question.Options
                .Where(option => !option.IsCorrect)
                .Select(option => option.Text)
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();
            var expected = chineseToEnglish
                ? new[] { "put off", "take off", "turn down" }
                : new[] { "postpone", "remove", "decline" };
            Assert.Equal(expected.OrderBy(text => text, StringComparer.Ordinal), drawn);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_SameEntryKind_WordTarget_ExcludesPhrases(
        bool chineseToEnglish)
    {
        var data = await SeedKindBookAsync();

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var question = await _service.CreateQuestionAsync(
                data.WordTarget.Word.Id,
                data.Book.Id,
                chineseToEnglish,
                unitId: data.Unit.Id,
                sameEntryKind: true);

            var drawn = question.Options
                .Where(option => !option.IsCorrect)
                .Select(option => option.Text)
                .ToList();
            string[] phraseTexts = chineseToEnglish
                ? ["put off", "take off", "turn down", "look forward to"]
                : ["postpone", "remove", "decline", "anticipate"];
            // A phrase option next to a word stem gives the answer away, which
            // is what the flag exists to prevent.
            Assert.Equal(3, drawn.Count);
            Assert.DoesNotContain(drawn, option => phraseTexts.Contains(option));
        }
    }

    [Fact]
    public async Task CreateQuestionAsync_SameEntryKind_UnclassifiedTarget_IsAskedAsAWord()
    {
        var data = await SeedKindBookAsync();

        var question = await _service.CreateQuestionAsync(
            data.UnclassifiedTarget.Word.Id,
            data.Book.Id,
            chineseToEnglish: true,
            unitId: data.Unit.Id,
            sameEntryKind: true);

        Assert.Equal(4, question.Options.Count);
        Assert.Single(question.Options, option => option.IsCorrect);
        // No position at all is not grounds to guess a phrase: the target is
        // asked as a word and the phrases of the unit stay out.
        var drawn = question.Options
            .Where(option => !option.IsCorrect)
            .Select(option => option.Text)
            .ToList();
        var phrases = new[] { "put off", "take off", "turn down", "look forward to" };
        Assert.DoesNotContain(drawn, option => phrases.Contains(option));
    }

    [Fact]
    public async Task CreateQuestionAsync_SameEntryKind_TargetWithWordAndPhrasePositions_IsAPhrase()
    {
        var data = await SeedKindBookAsync();
        // One meaning, two positions of one unit: the word kind of Section A
        // and the phrase kind of Section B. One phrase position anywhere in the
        // scope makes the target a phrase.
        await AssignAsync(data.Book.Id, data.Unit.Id, data.WordTarget.Meaning.Id,
            section: "B", entryKind: "phrase");

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var question = await _service.CreateQuestionAsync(
                data.WordTarget.Word.Id,
                data.Book.Id,
                chineseToEnglish: true,
                unitId: data.Unit.Id,
                sameEntryKind: true);

            // Four phrase candidates sit in the unit now — its three phrases
            // and the phrase target itself — and three are drawn, so the
            // assertion is the set they all come from.
            var drawn = question.Options
                .Where(option => !option.IsCorrect)
                .Select(option => option.Text)
                .ToList();
            var phrases = new HashSet<string>(
                ["put off", "take off", "turn down", "look forward to"], StringComparer.Ordinal);
            Assert.Equal(3, drawn.Count);
            Assert.All(drawn, option => Assert.Contains(option, phrases));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_SameEntryKind_WithoutUnit_UsesTheBookWideKind(
        bool chineseToEnglish)
    {
        var data = await SeedKindBookAsync();

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var question = await _service.CreateQuestionAsync(
                data.PhraseTarget.Word.Id,
                data.Book.Id,
                chineseToEnglish,
                sameEntryKind: true);

            var drawn = question.Options
                .Where(option => !option.IsCorrect)
                .Select(option => option.Text)
                .ToList();
            // Without a unit the kind is read from the whole book, so the
            // phrases of every unit qualify and no word does. Words in one
            // direction, their meanings in the other.
            string[] phraseTexts = chineseToEnglish
                ? ["put off", "take off", "turn down", "carry out"]
                : ["postpone", "remove", "decline", "perform"];
            Assert.Equal(3, drawn.Count);
            Assert.All(drawn, option => Assert.Contains(option, phraseTexts));
        }
    }

    [Fact]
    public async Task CreateQuestionAsync_SameEntryKind_TargetPhraseInAnotherUnit_BookWideKindIsPhrase()
    {
        var data = await SeedKindBookAsync();
        var otherUnit = await CreateUnitAsync(data.Book.Id, number: 3);
        // A word-classified position in one unit and a phrase position in
        // another: scoped to either single unit the kinds disagree, and the
        // book-wide read has to say phrase, because one phrase position
        // anywhere in the book is enough.
        await AssignAsync(data.Book.Id, otherUnit.Id, data.WordTarget.Meaning.Id,
            entryKind: "word");
        await AssignAsync(data.Book.Id, data.Unit.Id, data.WordTarget.Meaning.Id,
            section: "B", entryKind: "phrase");

        var question = await _service.CreateQuestionAsync(
            data.WordTarget.Word.Id,
            data.Book.Id,
            chineseToEnglish: true,
            sameEntryKind: true);

        Assert.Equal(4, question.Options.Count);
        // Five phrase candidates sit in the book — the three of the first unit,
        // the phrase target itself, and the phrase of the other unit — and
        // three are drawn, so the assertion is the set they all come from
        // rather than one arrangement of it.
        var drawn = question.Options
            .Where(option => !option.IsCorrect)
            .Select(option => option.Text)
            .ToList();
        var phrases = new HashSet<string>(
            ["carry out", "look forward to", "put off", "take off", "turn down"], StringComparer.Ordinal);
        Assert.Equal(3, drawn.Count);
        Assert.All(drawn, option => Assert.Contains(option, phrases));
    }

    // ==================== all query paths under the scope ====================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_UnitScope_LargerPool_HoldsTheInvariants(bool chineseToEnglish)
    {
        // A unit with more candidates than the window reads (3 x 4), so the
        // draws come from the random-window path and its wrap-around, and every
        // one of them has to keep the common invariants under the new filters.
        var book = await CreateBookAsync();
        var unit = await CreateUnitAsync(book.Id);
        var correct = await SeedWordAsync(book.Id, "apple", "fruit");
        var unitWords = new List<(string Word, string Meaning)>();
        for (var index = 0; index < 20; index++)
        {
            unitWords.Add(($"word{index:D2}", $"meaning {index:D2}"));
        }

        foreach (var (word, meaning) in unitWords)
        {
            var seeded = await SeedWordAsync(book.Id, word, meaning);
            await AssignAsync(book.Id, unit.Id, seeded.Meaning.Id);
        }

        var unitWordTexts = unitWords.Select(item => item.Word).ToHashSet(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var question = await _service.CreateQuestionAsync(
                correct.Word.Id, book.Id, chineseToEnglish, unitId: unit.Id);

            Assert.Equal(4, question.Options.Count);
            Assert.Equal(4, question.Options.Select(option => option.Text).Distinct().Count());
            var distractors = question.Options.Where(option => !option.IsCorrect).ToList();
            Assert.Equal(3, distractors.Count);
            // Every distractor comes from the unit, none is the target word,
            // none carries the asked meaning, and no word appears twice.
            Assert.All(distractors, option =>
                Assert.True(chineseToEnglish
                    ? unitWordTexts.Contains(option.Text)
                    : unitWords.Any(item => item.Meaning == option.Text)));
            Assert.DoesNotContain(distractors, option => option.Text == "apple");
            Assert.DoesNotContain(distractors, option => option.Text == "fruit");
            Assert.Equal(
                3,
                distractors.Select(option => option.WordId).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateQuestionAsync_UnitScopeCrowdedOutOfTheWindow_FallsBackToTheExhaustiveQuery(
        bool chineseToEnglish)
    {
        var book = await CreateBookAsync();
        var unit = await CreateUnitAsync(book.Id);
        var correct = await SeedWordAsync(book.Id, "apple", "fruit");
        // Sixty unit members all carrying the asked meaning are ineligible, and
        // three that are not. The window samples words before their definitions
        // are filtered, so it usually comes back with nothing usable and the
        // exhaustive query has to finish the job — under the same unit filter,
        // which is what this pins.
        for (var index = 0; index < 60; index++)
        {
            var seeded = await SeedWordAsync(book.Id, $"synonym{index:D2}", "fruit");
            await AssignAsync(book.Id, unit.Id, seeded.Meaning.Id);
        }

        var usable = new[]
        {
            ("banana", "yellow fruit"),
            ("cherry", "red fruit"),
            ("date", "sweet fruit")
        };
        foreach (var (word, meaning) in usable)
        {
            var seeded = await SeedWordAsync(book.Id, word, meaning);
            await AssignAsync(book.Id, unit.Id, seeded.Meaning.Id);
        }

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var question = await _service.CreateQuestionAsync(
                correct.Word.Id, book.Id, chineseToEnglish, unitId: unit.Id);

            // Answering 422 here would be the sampler's mistake, not the
            // unit's: three valid distractors exist on every one of these runs.
            var drawn = question.Options
                .Where(option => !option.IsCorrect)
                .Select(option => option.Text)
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();
            var expected = chineseToEnglish
                ? usable.Select(item => item.Item1).ToArray()
                : usable.Select(item => item.Item2).ToArray();
            Assert.Equal(expected.OrderBy(text => text, StringComparer.Ordinal), drawn);
        }
    }

    [Fact]
    public async Task CreateQuestionAsync_PhraseScopeCrowdedOutOfTheWindow_FallsBackToTheExhaustiveQuery()
    {
        var data = await SeedKindBookAsync();
        // The same crowding as above, with the kind filter stacked on the unit
        // filter: sixty phrase-classified words all carrying one shared
        // definition crowd the window out — every one of them passes the kind
        // filter, and each collides with the others on text, so the window's
        // one-meaning-per-word draw fills one slot instead of three and the
        // exhaustive query has to apply both filters again to finish the job.
        for (var index = 0; index < 60; index++)
        {
            var seeded = await SeedWordAsync(
                data.Book.Id, $"shared{index:D2}", "shared meaning", partOfSpeech: null);
            await AssignAsync(data.Book.Id, data.Unit.Id, seeded.Meaning.Id, entryKind: "phrase");
        }

        var eligible = new HashSet<string>(
            ["shared meaning", "postpone", "remove", "decline"], StringComparer.Ordinal);
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var question = await _service.CreateQuestionAsync(
                data.PhraseTarget.Word.Id,
                data.Book.Id,
                chineseToEnglish: false,
                unitId: data.Unit.Id,
                sameEntryKind: true);

            // Answering 422 here would be the sampler's mistake, not the
            // unit's or the kind filter's: three valid phrase distractors
            // exist on every one of these runs.
            Assert.Equal(4, question.Options.Count);
            var drawn = question.Options
                .Where(option => !option.IsCorrect)
                .Select(option => option.Text)
                .ToList();
            Assert.Equal(3, drawn.Count);
            Assert.All(drawn, option => Assert.Contains(option, eligible));
        }
    }

    // ==================== seeds ====================

    private async Task<(VocabularyModel Word, VocabularyMeaningModel Meaning)> SeedWordAsync(
        string bookId,
        string word,
        string meaning,
        string? partOfSpeech = "noun")
    {
        return await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = word },
            new VocabularyMeaningModel
            {
                BookId = bookId,
                PartOfSpeech = partOfSpeech,
                Meaning = meaning
            });
    }

    private async Task<VocabularyBookUnitModel> CreateUnitAsync(string bookId, int number = 1)
    {
        var now = DateTimeOffset.UtcNow;
        var unit = new VocabularyBookUnitModel
        {
            Id = Guid.NewGuid().ToString(),
            BookId = bookId,
            Number = number,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _bookUnitRepository.AddAsync(unit);
        await _unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
        return unit;
    }

    private async Task AssignAsync(
        string bookId,
        string unitId,
        string meaningId,
        string? section = null,
        string? entryKind = null)
    {
        await _meaningUnitRepository.AddAsync(new VocabularyMeaningUnitModel
        {
            UnitId = unitId,
            MeaningId = meaningId,
            BookId = bookId,
            Section = section,
            EntryKind = entryKind
        });
        await _unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One word with a noun and a verb sense, each with its own distractors.</summary>
    private async Task<(string BookId, string WordId, string VerbMeaningId)> SeedMultiSenseBookAsync()
    {
        var book = await CreateBookAsync();
        var bank = await SeedWordAsync(book.Id, "bank", "money place", "n.");
        var (_, verbMeaning) = await SeedWordAsync(book.Id, "bank", "to save", "v.");
        _ = await SeedWordAsync(book.Id, "depository", "money place", "n.");
        _ = await SeedWordAsync(book.Id, "hoard", "to save", "v.");
        _ = await SeedWordAsync(book.Id, "apple", "red fruit", "n.");
        _ = await SeedWordAsync(book.Id, "cherry", "small fruit", "n.");
        _ = await SeedWordAsync(book.Id, "date", "sweet fruit", "n.");
        return (book.Id, bank.Word.Id, verbMeaning.Id);
    }

    private sealed record UnitBookData(
        VocabularyBookModel Book,
        VocabularyModel CorrectWord,
        VocabularyBookUnitModel Unit,
        Dictionary<string, (VocabularyModel Word, VocabularyMeaningModel Meaning)> UnitWords);

    /// <summary>
    /// One unit holding a few words, a correct word with no unit position, and
    /// book members the unit does not contain.
    /// </summary>
    private async Task<UnitBookData> SeedUnitBookAsync(int unitWordCount = 3)
    {
        var book = await CreateBookAsync();
        var unit = await CreateUnitAsync(book.Id);
        var correct = await SeedWordAsync(book.Id, "apple", "fruit");

        var unitWords =
            new Dictionary<string, (VocabularyModel, VocabularyMeaningModel)>(StringComparer.Ordinal);
        var names = new[] { "banana", "cherry", "date", "elderberry" };
        var meanings = new[] { "yellow fruit", "red fruit", "sweet fruit", "dark fruit" };
        for (var index = 0; index < unitWordCount && index < names.Length; index++)
        {
            var seeded = await SeedWordAsync(book.Id, names[index], meanings[index]);
            await AssignAsync(book.Id, unit.Id, seeded.Meaning.Id);
            unitWords[names[index]] = seeded;
        }

        // Book members outside the unit: a smaller draw must never reach them.
        _ = await SeedWordAsync(book.Id, "fig", "soft fruit");
        _ = await SeedWordAsync(book.Id, "grape", "vine fruit");
        return new UnitBookData(book, correct.Word, unit, unitWords);
    }

    private sealed record KindBookData(
        VocabularyBookModel Book,
        VocabularyBookUnitModel Unit,
        (VocabularyModel Word, VocabularyMeaningModel Meaning) PhraseTarget,
        (VocabularyModel Word, VocabularyMeaningModel Meaning) WordTarget,
        (VocabularyModel Word, VocabularyMeaningModel Meaning) UnclassifiedTarget);

    /// <summary>
    /// One unit holding three phrases, two classified words and one
    /// unclassified word; a second unit holding one more phrase, so the
    /// book-wide kind read sees four phrases in all.
    /// </summary>
    private async Task<KindBookData> SeedKindBookAsync()
    {
        var book = await CreateBookAsync();
        var unit = await CreateUnitAsync(book.Id);
        var otherUnit = await CreateUnitAsync(book.Id, number: 2);

        var phraseTarget = await SeedWordAsync(book.Id, "look forward to", "anticipate");
        await AssignAsync(book.Id, unit.Id, phraseTarget.Meaning.Id, entryKind: "phrase");

        var phrases = new[]
        {
            ("put off", "postpone"),
            ("take off", "remove"),
            ("turn down", "decline")
        };
        foreach (var (word, meaning) in phrases)
        {
            var seeded = await SeedWordAsync(book.Id, word, meaning, partOfSpeech: null);
            await AssignAsync(book.Id, unit.Id, seeded.Meaning.Id, entryKind: "phrase");
        }

        var wordTarget = await SeedWordAsync(book.Id, "banana", "yellow fruit");
        await AssignAsync(book.Id, unit.Id, wordTarget.Meaning.Id, entryKind: "word");
        var cherry = await SeedWordAsync(book.Id, "cherry", "red fruit");
        await AssignAsync(book.Id, unit.Id, cherry.Meaning.Id);
        var date = await SeedWordAsync(book.Id, "date", "sweet fruit");
        await AssignAsync(book.Id, unit.Id, date.Meaning.Id);

        // The unclassified target sits in the unit with no kind at all.
        var unclassifiedTarget = await SeedWordAsync(book.Id, "elderberry", "dark fruit");
        await AssignAsync(book.Id, unit.Id, unclassifiedTarget.Meaning.Id);

        // A phrase the first unit does not hold, so a book-wide kind read finds
        // a distractor no unit read can offer.
        var otherPhrase = await SeedWordAsync(book.Id, "carry out", "perform", partOfSpeech: null);
        await AssignAsync(book.Id, otherUnit.Id, otherPhrase.Meaning.Id, entryKind: "phrase");

        return new KindBookData(book, unit, phraseTarget, wordTarget, unclassifiedTarget);
    }
}
