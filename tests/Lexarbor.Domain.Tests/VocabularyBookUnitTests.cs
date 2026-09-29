using System;
using System.Linq;
using System.Threading.Tasks;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class VocabularyBookUnitTests : TestBase
{
    private readonly VocabularyBookUnitDomainService _service;
    private readonly VocabularyDomainService _importService;

    public VocabularyBookUnitTests()
    {
        _service = new VocabularyBookUnitDomainService(
            _bookRepository,
            _meaningRepository,
            _bookUnitRepository,
            _meaningUnitRepository,
            _unitOfWork);
        _importService = new VocabularyDomainService(
            _vocabularyRepository,
            _bookRepository,
            _meaningRepository,
            _bookUnitRepository,
            _meaningUnitRepository,
            _unitOfWork);
    }

    [Fact]
    public async Task ExactPositionMoveAndRemove_PreserveEverySibling()
    {
        var book = await CreateBookAsync();
        var first = await _service.CreateAsync(book.Id, 2, null);
        var second = await _service.CreateAsync(book.Id, 6, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "come true" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "happen" });
        await _service.AssignMeaningAsync(first.Id, meaning.Id, "A", "phrase");
        await _service.AssignMeaningAsync(first.Id, meaning.Id, "A", "word");
        await _service.AssignMeaningAsync(first.Id, meaning.Id, "B", "phrase");
        await _service.AssignMeaningAsync(second.Id, meaning.Id, null, "phrase");

        await _service.MovePositionAsync(book.Id, meaning.Id, first.Id, "A", "phrase",
            second.Id, "A", "word", TestContext.Current.CancellationToken);
        await _service.MovePositionAsync(book.Id, meaning.Id, second.Id, "A", "word",
            second.Id, "A", "word", TestContext.Current.CancellationToken);
        Assert.Equal(4, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
        Assert.False(await _meaningUnitRepository.ExistsAsync(first.Id, meaning.Id, "A", "phrase"));
        Assert.True(await _meaningUnitRepository.ExistsAsync(first.Id, meaning.Id, "A", "word"));
        Assert.True(await _meaningUnitRepository.ExistsAsync(second.Id, meaning.Id, "A", "word"));

        await Assert.ThrowsAsync<ConflictException>(() => _service.MovePositionAsync(book.Id, meaning.Id,
            first.Id, "B", "phrase", second.Id, "A", "word", TestContext.Current.CancellationToken));
        Assert.True(await _meaningUnitRepository.ExistsAsync(first.Id, meaning.Id, "B", "phrase"));
        await _service.RemovePositionAsync(book.Id, meaning.Id, first.Id, "A", "word", TestContext.Current.CancellationToken);
        Assert.False(await _meaningUnitRepository.ExistsAsync(first.Id, meaning.Id, "A", "word"));
        Assert.True(await _meaningUnitRepository.ExistsAsync(first.Id, meaning.Id, "B", "phrase"));
        Assert.True(await _meaningUnitRepository.ExistsAsync(second.Id, meaning.Id, null, "phrase"));
        Assert.NotNull(await _meaningRepository.GetByIdAsync(meaning.Id));
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.RemovePositionAsync(book.Id,
            meaning.Id, first.Id, "A", "word", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExactPositionOwnershipValidationAndCancellation_LeaveSourceUntouched()
    {
        var book = await CreateBookAsync();
        var other = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 1, null);
        var foreignUnit = await _service.CreateAsync(other.Id, 1, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "run into" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "meet" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, "phrase");
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.MovePositionAsync(book.Id,
            meaning.Id, unit.Id, null, "phrase", foreignUnit.Id, null, "word", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.RemovePositionAsync(other.Id,
            meaning.Id, unit.Id, null, "phrase", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DomainValidationException>(() => _service.MovePositionAsync(book.Id,
            meaning.Id, unit.Id, "C", "phrase", unit.Id, "A", "word", TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.RemovePositionAsync(book.Id,
            meaning.Id, unit.Id, null, "phrase", cancelled.Token));
        Assert.True(await _meaningUnitRepository.ExistsAsync(unit.Id, meaning.Id, null, "phrase"));
    }

    [Fact]
    public async Task CreateAndRead_StoresEightUnitsInUnitOrder()
    {
        var book = await CreateBookAsync();

        for (var number = 1; number <= 8; number++)
        {
            var unit = await _service.CreateAsync(book.Id, number, number == 2 ? " Unit 2 " : null);
            Assert.Equal(book.Id, unit.BookId);
            Assert.Equal(number, unit.Number);
            // Whitespace-only titles count as absent; real ones are trimmed to
            // display text.
            Assert.Equal(number == 2 ? "Unit 2" : null, unit.Title);
        }

        var units = await _service.GetByBookAsync(book.Id);
        Assert.Equal(8, units.Count);
        Assert.Equal(Enumerable.Range(1, 8), units.Select(unit => unit.Number));
    }

    [Fact]
    public async Task Create_DuplicateNumberWithinOneBook_IsRejected()
    {
        var book = await CreateBookAsync();
        await _service.CreateAsync(book.Id, 2, null);

        await Assert.ThrowsAsync<ConflictException>(
            () => _service.CreateAsync(book.Id, 2, "Second try"));
        Assert.Single(await _service.GetByBookAsync(book.Id));
    }

    [Fact]
    public async Task Create_SameNumberInDifferentBooks_Coexists()
    {
        var first = await CreateBookAsync();
        var second = await CreateBookAsync();

        await _service.CreateAsync(first.Id, 6, null);
        var otherBookUnit = await _service.CreateAsync(second.Id, 6, null);

        var firstUnits = await _service.GetByBookAsync(first.Id);
        var secondUnits = await _service.GetByBookAsync(second.Id);
        Assert.Equal(6, Assert.Single(firstUnits).Number);
        Assert.Equal(otherBookUnit.Id, Assert.Single(secondUnits).Id);
        Assert.Equal(2, await _dbContext.VocabularyBookUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_NonPositiveNumberOrMissingBook_IsRejected()
    {
        var book = await CreateBookAsync();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => _service.CreateAsync(book.Id, 0, null));
        await Assert.ThrowsAsync<DomainValidationException>(
            () => _service.CreateAsync(book.Id, -3, null));
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.CreateAsync("no-such-book", 1, null));
        Assert.Empty(await _service.GetByBookAsync(book.Id));
    }

    [Fact]
    public async Task AssignMeaning_TwoUnitsShareOneEquivalentMeaning()
    {
        var book = await CreateBookAsync();
        var unit2 = await _service.CreateAsync(book.Id, 2, null);
        var unit6 = await _service.CreateAsync(book.Id, 6, null);

        // The same entry imported twice resolves to one meaning row.
        var (_, firstImport) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "come true" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "phrase", Meaning = "（梦想等）实现" });
        var (_, secondImport) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "come true" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "phrase", Meaning = "（梦想等）实现" });
        Assert.Equal(firstImport.Id, secondImport.Id);

        await _service.AssignMeaningAsync(unit2.Id, firstImport.Id, null, null);
        await _service.AssignMeaningAsync(unit6.Id, firstImport.Id, null, null);

        Assert.Equal(1, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));

        // Replaying either assignment changes nothing.
        await _service.AssignMeaningAsync(unit2.Id, firstImport.Id, null, null);
        await _service.AssignMeaningAsync(unit6.Id, firstImport.Id, null, null);
        Assert.Equal(2, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignMeaning_SameUnitDifferentSections_AreTwoIdempotentPositions()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 2, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "come true" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "phrase", Meaning = "（梦想等）实现" });

        // The unsectioned place and the two sections of one unit are three
        // positions of one meaning; replaying any of them writes nothing.
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, "A", null);
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, "B", null);
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, "A", null);
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, " B ", null);

        Assert.Equal(1, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignMeaning_SamePlaceDifferentKinds_AreTwoIdempotentPositions()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 2, null);
        var otherUnit = await _service.CreateAsync(book.Id, 6, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "come true" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "phrase", Meaning = "（梦想等）实现" });

        // The unclassified kind and the two kinds of one place are three
        // positions of one meaning — the kind never duplicates the meaning —
        // and a padded kind trims to the same position; replaying any of them
        // writes nothing, and other units' assignments are untouched.
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, "word");
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, "phrase");
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, "word");
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, " word ");
        await _service.AssignMeaningAsync(otherUnit.Id, meaning.Id, null, null);

        Assert.Equal(1, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignMeaning_InvalidEntryKind_IsRejectedWithoutWrites()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 2, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });

        // Case is significant — Word is not normalized into word — and neither
        // a third kind nor a part of speech is a kind.
        foreach (var entryKind in new[] { "Word", "PHRASE", "words", "verb" })
        {
            await Assert.ThrowsAsync<DomainValidationException>(
                () => _service.AssignMeaningAsync(unit.Id, meaning.Id, null, entryKind));
        }

        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignMeaning_InvalidSection_IsRejectedWithoutWrites()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 2, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });

        foreach (var section in new[] { "a", "C", "AB", "A/B" })
        {
            await Assert.ThrowsAsync<DomainValidationException>(
                () => _service.AssignMeaningAsync(unit.Id, meaning.Id, section, null));
        }

        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Removing one position of a meaning leaves the meaning, the unit, and the
    /// meaning's other positions of the same unit alone.
    /// </summary>
    [Fact]
    public async Task RemoveMeaning_OneSectionPosition_RemovesOnlyThatPosition()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 2, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, "A", null);
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, "B", null);

        await _service.RemoveMeaningAsync(unit.Id, meaning.Id, "A", null);

        var remaining = await _meaningUnitRepository.GetByMeaningIdAsync(meaning.Id);
        Assert.Equal([null, "B"], remaining.Select(membership => membership.Section).Order());
        Assert.NotNull(await _meaningRepository.GetByIdAsync(meaning.Id));
        Assert.NotNull(await _bookUnitRepository.GetByIdAsync(unit.Id));
        // The removed position is gone for good; removing it again is the same
        // not-found as any other missing assignment.
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.RemoveMeaningAsync(unit.Id, meaning.Id, "A", null));
    }

    /// <summary>
    /// Removing one kind of one place leaves the meaning, the other kind of
    /// that place, and the meaning's assignments to other units alone.
    /// </summary>
    [Fact]
    public async Task RemoveMeaning_OneKindPosition_RemovesOnlyThatKind()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 2, null);
        var otherUnit = await _service.CreateAsync(book.Id, 6, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, "A", null);
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, "A", "word");
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, "A", "phrase");
        await _service.AssignMeaningAsync(otherUnit.Id, meaning.Id, null, null);

        await _service.RemoveMeaningAsync(unit.Id, meaning.Id, "A", "word");

        var remaining = await _meaningUnitRepository.GetByMeaningIdAsync(meaning.Id);
        Assert.Equal(
            [("A", null), ("A", "phrase")],
            remaining
                .Where(membership => membership.UnitId == unit.Id)
                .Select(membership => (membership.Section, membership.EntryKind))
                .OrderBy(t => t.EntryKind ?? ""));
        Assert.Equal(
            [(null, null)],
            remaining
                .Where(membership => membership.UnitId == otherUnit.Id)
                .Select(membership => (membership.Section, membership.EntryKind)));
        Assert.NotNull(await _meaningRepository.GetByIdAsync(meaning.Id));
        // The removed kind is gone for good; removing it again is the same
        // not-found as any other missing assignment.
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.RemoveMeaningAsync(unit.Id, meaning.Id, "A", "word"));
    }

    [Fact]
    public async Task AssignMeaning_CrossBookAssignment_IsRejectedAndLeavesNoRow()
    {
        var bookA = await CreateBookAsync();
        var bookB = await CreateBookAsync();
        var unitInA = await _service.CreateAsync(bookA.Id, 1, null);
        var (_, meaningInB) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = bookB.Id, PartOfSpeech = "n.", Meaning = "苹果" });

        await Assert.ThrowsAsync<ConflictException>(
            () => _service.AssignMeaningAsync(unitInA.Id, meaningInB.Id, null, null));

        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignMeaning_UnknownUnitOrMeaning_IsRejected()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 1, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.AssignMeaningAsync("no-such-unit", meaning.Id, null, null));
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.AssignMeaningAsync(unit.Id, "no-such-meaning", null, null));
        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The domain service is the intended path, but the database has to keep a
    /// cross-book assignment unrepresentable even for a caller that writes past
    /// it, and a rejected save must not leave the other rows of the same save
    /// behind.
    /// </summary>
    [Fact]
    public async Task Membership_WrittenAcrossBooksAtTheDatabaseLevel_IsRejectedEntirely()
    {
        var bookA = await CreateBookAsync();
        var bookB = await CreateBookAsync();
        var unitInA = await _service.CreateAsync(bookA.Id, 1, null);
        var (_, meaningInB) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = bookB.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        var (_, meaningInA) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "banana" },
            new VocabularyMeaningModel { BookId = bookA.Id, PartOfSpeech = "n.", Meaning = "香蕉" });

        // One legitimate assignment and one that moves book B's meaning into
        // book A's unit, saved together: the composite foreign keys reject the
        // pair and the implicit transaction keeps the legitimate half from
        // surviving on its own.
        await _meaningUnitRepository.AddAsync(new VocabularyMeaningUnitModel
        {
            UnitId = unitInA.Id,
            MeaningId = meaningInA.Id,
            BookId = bookA.Id
        });
        await _meaningUnitRepository.AddAsync(new VocabularyMeaningUnitModel
        {
            UnitId = unitInA.Id,
            MeaningId = meaningInB.Id,
            BookId = bookB.Id
        });

        await Assert.ThrowsAsync<ConflictException>(
            () => _unitOfWork.SaveChangesAsync());
        _dbContext.ChangeTracker.Clear();

        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteUnit_RemovesOnlyItsAssignments()
    {
        var book = await CreateBookAsync();
        var unit2 = await _service.CreateAsync(book.Id, 2, null);
        var unit6 = await _service.CreateAsync(book.Id, 6, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "come true" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "phrase", Meaning = "（梦想等）实现" });
        await _service.AssignMeaningAsync(unit2.Id, meaning.Id, null, null);
        await _service.AssignMeaningAsync(unit6.Id, meaning.Id, null, null);

        await _service.DeleteAsync(book.Id, unit2.Id);

        Assert.Null(await _bookUnitRepository.GetByIdAsync(unit2.Id));
        // The meaning, its word, and the surviving unit's assignment are all
        // still there.
        Assert.NotNull(await _meaningRepository.GetByIdAsync(meaning.Id));
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        var remaining = await _meaningUnitRepository.GetByUnitIdAsync(unit6.Id);
        Assert.Equal(meaning.Id, Assert.Single(remaining).MeaningId);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.DeleteAsync(book.Id, unit2.Id));
    }

    [Fact]
    public async Task Update_KeepsOwnNumberAndNormalizesTitle()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 2, " Old title ");

        // Keeping the unit's own number is a plain replace of the other field,
        // and a whitespace-only title normalizes to null like on create.
        var (updated, meaningCount) = await _service.UpdateAsync(book.Id, unit.Id, 2, "  ");
        Assert.Equal(2, updated.Number);
        Assert.Null(updated.Title);
        Assert.Equal(0, meaningCount);

        var stored = await _bookUnitRepository.GetByIdAsync(unit.Id);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Number);
        Assert.Null(stored.Title);
        Assert.True(stored.UpdatedAt >= unit.UpdatedAt);

        // A real renumber onto a free number succeeds and carries the title.
        var (renumbered, _) = await _service.UpdateAsync(book.Id, unit.Id, 3, "Unit 3");
        Assert.Equal(3, renumbered.Number);
        Assert.Equal("Unit 3", renumbered.Title);
    }

    [Fact]
    public async Task Update_RenumberOntoAnotherUnit_IsRejectedWithoutRewrites()
    {
        var book = await CreateBookAsync();
        var unit2 = await _service.CreateAsync(book.Id, 2, "Second");
        var unit6 = await _service.CreateAsync(book.Id, 6, "Sixth");

        await Assert.ThrowsAsync<ConflictException>(
            () => _service.UpdateAsync(book.Id, unit2.Id, 6, "Moved"));

        var stored = await _bookUnitRepository.GetByIdAsync(unit2.Id);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Number);
        Assert.Equal("Second", stored.Title);
        var other = await _bookUnitRepository.GetByIdAsync(unit6.Id);
        Assert.NotNull(other);
        Assert.Equal(6, other.Number);
        Assert.Equal("Sixth", other.Title);
    }

    [Fact]
    public async Task Update_MismatchedPathOrInvalidNumber_IsRejected()
    {
        var bookA = await CreateBookAsync();
        var bookB = await CreateBookAsync();
        var unitInA = await _service.CreateAsync(bookA.Id, 1, null);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.UpdateAsync("no-such-book", unitInA.Id, 2, null));
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.UpdateAsync(bookA.Id, "no-such-unit", 2, null));
        await Assert.ThrowsAsync<ConflictException>(
            () => _service.UpdateAsync(bookB.Id, unitInA.Id, 2, null));
        await Assert.ThrowsAsync<DomainValidationException>(
            () => _service.UpdateAsync(bookA.Id, unitInA.Id, 0, null));
        await Assert.ThrowsAsync<DomainValidationException>(
            () => _service.UpdateAsync(bookA.Id, unitInA.Id, -3, null));

        var stored = await _bookUnitRepository.GetByIdAsync(unitInA.Id);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Number);
        Assert.Null(stored.Title);
    }

    [Fact]
    public async Task GetByBookWithCounts_CountsAssignmentsPerUnit()
    {
        var book = await CreateBookAsync();
        var unit2 = await _service.CreateAsync(book.Id, 2, null);
        var unit3 = await _service.CreateAsync(book.Id, 3, null);
        var unit6 = await _service.CreateAsync(book.Id, 6, null);
        var (_, apple) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        var (_, tree) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple tree" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果树" });
        await _service.AssignMeaningAsync(unit3.Id, apple.Id, null, null);
        await _service.AssignMeaningAsync(unit3.Id, tree.Id, null, null);
        await _service.AssignMeaningAsync(unit6.Id, apple.Id, null, null);
        // The same meaning twice in one unit — its A place and its unsectioned
        // place — is still one meaning of that unit, so the count stays put.
        await _service.AssignMeaningAsync(unit3.Id, apple.Id, "A", null);
        await _service.AssignMeaningAsync(unit3.Id, apple.Id, "B", null);

        var rows = await _service.GetByBookWithCountsAsync(book.Id);
        // Unit order by number, counts from the same grouped read, and a unit
        // with no assignments reads as zero rather than being skipped.
        Assert.Equal(new[] { 2, 3, 6 }, rows.Select(row => row.Unit.Number));
        Assert.Equal(new[] { 0, 2, 1 }, rows.Select(row => row.MeaningCount));
        Assert.Equal(unit3.Id, rows[1].Unit.Id);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.GetByBookWithCountsAsync("no-such-book"));
    }

    [Fact]
    public async Task Delete_MismatchedPath_IsRejectedWithoutRemoval()
    {
        var bookA = await CreateBookAsync();
        var bookB = await CreateBookAsync();
        var unitInA = await _service.CreateAsync(bookA.Id, 1, null);

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.DeleteAsync("no-such-book", unitInA.Id));
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.DeleteAsync(bookA.Id, "no-such-unit"));
        // The unit exists, but it belongs to book A: book B's path is refused
        // with a conflict instead of deleting it.
        await Assert.ThrowsAsync<ConflictException>(
            () => _service.DeleteAsync(bookB.Id, unitInA.Id));

        Assert.NotNull(await _bookUnitRepository.GetByIdAsync(unitInA.Id));
    }

    [Fact]
    public async Task RemoveMeaning_RemovesOneAssignmentOnly()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 1, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);

        await _service.RemoveMeaningAsync(unit.Id, meaning.Id, null, null);

        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await _meaningRepository.GetByIdAsync(meaning.Id));
        Assert.NotNull(await _bookUnitRepository.GetByIdAsync(unit.Id));
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.RemoveMeaningAsync(unit.Id, meaning.Id, null, null));
    }

    [Fact]
    public async Task DeleteMeaning_EntityPath_CascadesItsAssignments()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 1, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);

        await _meaningRepository.DeleteAsync(meaning.Id);
        await _unitOfWork.SaveChangesAsync();

        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteWord_CascadesMeaningsAndAssignments()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 1, null);
        var (word, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);

        var wordEntity = await _dbContext.Vocabularies.FindAsync(
            [word.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(wordEntity);
        _dbContext.Vocabularies.Remove(wordEntity);
        await _unitOfWork.SaveChangesAsync();

        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await _bookUnitRepository.GetByIdAsync(unit.Id));
    }

    /// <summary>
    /// The cleanup repository deletes meanings with raw SQL, outside EF's
    /// change tracker. The membership rows have to follow through the database's
    /// own foreign keys, or that path would leave dangling assignments.
    /// </summary>
    [Fact]
    public async Task DeleteMeaning_RawSqlCleanupPath_CascadesItsAssignments()
    {
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 1, null);
        var (word, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);

        await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM vocabulary_meaning WHERE id = {0}", meaning.Id);

        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
        // The raw-SQL cleanup deletes orphan words itself; this path removed
        // only the meaning, so the shared word remains stored until a cleanup
        // pass removes it.
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(
            vocabulary => vocabulary.Id == word.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteBook_WithMeanings_StillConflicts()
    {
        var bookService = new VocabularyBookDomainService(_bookRepository, _unitOfWork);
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 1, null);
        var (_, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);

        await Assert.ThrowsAsync<ConflictException>(
            () => bookService.DeleteAsync(book.Id));
        Assert.NotNull(await _bookUnitRepository.GetByIdAsync(unit.Id));
    }

    [Fact]
    public async Task DeleteBook_WithUnitsButNoMeanings_CascadesUnitsAndAssignments()
    {
        var bookService = new VocabularyBookDomainService(_bookRepository, _unitOfWork);
        var book = await CreateBookAsync();
        var unit = await _service.CreateAsync(book.Id, 1, null);
        var (word, meaning) = await _importService.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, PartOfSpeech = "n.", Meaning = "苹果" });
        await _service.AssignMeaningAsync(unit.Id, meaning.Id, null, null);

        // The cleanup "clear" path: raw SQL removes the book's meanings and the
        // word that lost its last reference; the assignments follow through the
        // database's foreign keys.
        await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM vocabulary_meaning WHERE book_id = {0}", book.Id);
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM vocabulary WHERE id = {0}
              AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning WHERE vocabulary_id = {0})
            """,
            word.Id);
        _dbContext.ChangeTracker.Clear();
        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));

        // With no meanings left, the existing book delete rule allows the
        // delete, and the units follow it away.
        await bookService.DeleteAsync(book.Id);

        Assert.Null(await _bookRepository.GetByIdAsync(book.Id));
        Assert.Equal(0, await _dbContext.VocabularyBookUnits.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }
}
