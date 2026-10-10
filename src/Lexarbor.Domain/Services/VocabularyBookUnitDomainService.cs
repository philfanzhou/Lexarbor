using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

/// <summary>
/// The domain rules for book units and meaning-to-unit assignments. This
/// service owns the invariants later management, import and query tasks build
/// on: unit numbers are unique per book and positive, one meaning may be
/// assigned to many units of its own book and to several positions of one
/// unit — per section and per entry kind — and a repeated assignment of the
/// same position changes nothing.
/// </summary>
/// <remarks>
/// Cross-book assignments are rejected here with an explicit conflict and are
/// additionally unrepresentable at the database level, where both membership
/// foreign keys compare the same <c>book_id</c>. Every write runs inside the
/// serialized <see cref="IUnitOfWork"/> transaction, so a rejected or failed
/// operation leaves no partial rows behind.
/// </remarks>
public class VocabularyBookUnitDomainService
{
    private readonly IVocabularyBookRepository _bookRepository;
    private readonly IVocabularyMeaningRepository _meaningRepository;
    private readonly IVocabularyBookUnitRepository _unitRepository;
    private readonly IVocabularyMeaningUnitRepository _membershipRepository;
    private readonly IUnitOfWork _unitOfWork;

    public VocabularyBookUnitDomainService(
        IVocabularyBookRepository bookRepository,
        IVocabularyMeaningRepository meaningRepository,
        IVocabularyBookUnitRepository unitRepository,
        IVocabularyMeaningUnitRepository membershipRepository,
        IUnitOfWork unitOfWork)
    {
        _bookRepository = bookRepository;
        _meaningRepository = meaningRepository;
        _unitRepository = unitRepository;
        _membershipRepository = membershipRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<VocabularyBookUnitModel> CreateAsync(string bookId, int number, string? title)
    {
        var normalizedBookId = NormalizeRequired(bookId, "BookId is required.");
        if (number < 1)
        {
            throw new DomainValidationException("Unit number must be a positive integer.");
        }

        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();

        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            _ = await _bookRepository.GetByIdAsync(normalizedBookId)
                ?? throw new ResourceNotFoundException("Vocabulary book was not found.");

            // The unique index on (book_id, number) makes a concurrent duplicate
            // a 409 even though the serialized write lock means this check has
            // already seen the committed row.
            var existingNumbers = await _unitRepository.GetByBookIdAsync(normalizedBookId);
            if (existingNumbers.Any(unit => unit.Number == number))
            {
                throw new ConflictException(
                    "A unit with the same number already exists in this vocabulary book.");
            }

            var now = DateTimeOffset.UtcNow;
            var unit = new VocabularyBookUnitModel
            {
                Id = Guid.NewGuid().ToString(),
                BookId = normalizedBookId,
                Number = number,
                Title = normalizedTitle,
                CreatedAt = now,
                UpdatedAt = now
            };
            await _unitRepository.AddAsync(unit);
            await _unitOfWork.SaveChangesAsync();
            return unit;
        });
    }

    public async Task<List<VocabularyBookUnitModel>> GetByBookAsync(string bookId)
    {
        var normalizedBookId = NormalizeRequired(bookId, "BookId is required.");

        _ = await _bookRepository.GetByIdAsync(normalizedBookId)
            ?? throw new ResourceNotFoundException("Vocabulary book was not found.");

        var units = await _unitRepository.GetByBookIdAsync(normalizedBookId);
        return units.OrderBy(unit => unit.Number).ToList();
    }

    /// <summary>
    /// Lists a book's units in unit-number order together with each unit's
    /// assignment count. The counts come from one grouped read over the book's
    /// memberships, so listing a book costs two queries regardless of how many
    /// units it has.
    /// </summary>
    public async Task<List<(VocabularyBookUnitModel Unit, int MeaningCount)>> GetByBookWithCountsAsync(
        string bookId)
    {
        var normalizedBookId = NormalizeRequired(bookId, "BookId is required.");

        _ = await _bookRepository.GetByIdAsync(normalizedBookId)
            ?? throw new ResourceNotFoundException("Vocabulary book was not found.");

        var units = await _unitRepository.GetByBookIdAsync(normalizedBookId);
        var counts = await _unitRepository.GetAssignmentCountsByBookIdAsync(normalizedBookId);
        return units
            .OrderBy(unit => unit.Number)
            .Select(unit => (unit, counts.TryGetValue(unit.Id, out var count) ? count : 0))
            .ToList();
    }

    /// <summary>
    /// Replaces one unit's number and title. Keeping the unit's own number is
    /// not a conflict; taking a number another unit of the same book holds is.
    /// Ownership cannot move: a unit found under another book's path is a
    /// conflict, not a silent edit.
    /// </summary>
    public async Task<(VocabularyBookUnitModel Unit, int MeaningCount)> UpdateAsync(
        string bookId, string unitId, int number, string? title)
    {
        var normalizedBookId = NormalizeRequired(bookId, "BookId is required.");
        var normalizedUnitId = NormalizeRequired(unitId, "Unit ID is required.");
        if (number < 1)
        {
            throw new DomainValidationException("Unit number must be a positive integer.");
        }

        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();

        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            _ = await _bookRepository.GetByIdAsync(normalizedBookId)
                ?? throw new ResourceNotFoundException("Vocabulary book was not found.");

            var unit = await _unitRepository.GetByIdAsync(normalizedUnitId)
                ?? throw new ResourceNotFoundException("Vocabulary book unit was not found.");

            if (unit.BookId != normalizedBookId)
            {
                throw new ConflictException(
                    "Vocabulary book unit does not belong to the requested vocabulary book.");
            }

            // The same rule as create, minus this unit itself: keeping its own
            // number is a plain replace of the other field. The unique index on
            // (book_id, number) still backs a concurrent renumber.
            var existingNumbers = await _unitRepository.GetByBookIdAsync(normalizedBookId);
            if (existingNumbers.Any(other => other.Number == number && other.Id != unit.Id))
            {
                throw new ConflictException(
                    "A unit with the same number already exists in this vocabulary book.");
            }

            // Read inside the transaction that updates the unit, so the count a
            // replace answers with belongs to the same state as the edit.
            var counts = await _unitRepository.GetAssignmentCountsByBookIdAsync(normalizedBookId);

            unit.Number = number;
            unit.Title = normalizedTitle;
            unit.UpdatedAt = DateTimeOffset.UtcNow;
            await _unitRepository.UpdateAsync(unit);
            await _unitOfWork.SaveChangesAsync();
            return (unit, counts.TryGetValue(unit.Id, out var count) ? count : 0);
        });
    }

    /// <summary>
    /// Deletes one unit of the named book. Only the unit and its assignments
    /// disappear; meanings, shared words, and other units' assignments survive.
    /// </summary>
    public async Task DeleteAsync(string bookId, string unitId)
    {
        var normalizedBookId = NormalizeRequired(bookId, "BookId is required.");
        var normalizedUnitId = NormalizeRequired(unitId, "Unit ID is required.");

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            _ = await _bookRepository.GetByIdAsync(normalizedBookId)
                ?? throw new ResourceNotFoundException("Vocabulary book was not found.");

            var unit = await _unitRepository.GetByIdAsync(normalizedUnitId)
                ?? throw new ResourceNotFoundException("Vocabulary book unit was not found.");

            if (unit.BookId != normalizedBookId)
            {
                throw new ConflictException(
                    "Vocabulary book unit does not belong to the requested vocabulary book.");
            }

            // Assignments of this unit cascade away; meanings and words are
            // untouched, including meanings this unit shared with other units.
            await _unitRepository.DeleteAsync(unit.Id);
            await _unitOfWork.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>
    /// Assigns a meaning to one place of a unit: its Section A, its Section B,
    /// or — with a null <paramref name="section"/> — the unit as a whole, and —
    /// with an <paramref name="entryKind"/> — as a word or a phrase, or
    /// unclassified when null. Assigning a meaning to a place it already holds
    /// returns without writing, so replaying an assignment is idempotent; the
    /// same meaning may hold several places of the same unit. The meaning must
    /// belong to the unit's book. A section other than <c>A</c> or <c>B</c>
    /// after trimming is rejected rather than guessed at, matching the
    /// unit-number rule, and so is a kind other than <c>word</c> or
    /// <c>phrase</c>.
    /// </summary>
    public async Task AssignMeaningAsync(string unitId, string meaningId, string? section, string? entryKind)
    {
        var normalizedUnitId = NormalizeRequired(unitId, "Unit ID is required.");
        var normalizedMeaningId = NormalizeRequired(meaningId, "Meaning ID is required.");
        var normalizedSection = NormalizeSection(section);
        var normalizedEntryKind = NormalizeEntryKind(entryKind);

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var unit = await _unitRepository.GetByIdAsync(normalizedUnitId)
                ?? throw new ResourceNotFoundException("Vocabulary book unit was not found.");

            var meaning = await _meaningRepository.GetByIdAsync(normalizedMeaningId)
                ?? throw new ResourceNotFoundException("Vocabulary meaning was not found.");

            if (meaning.BookId != unit.BookId)
            {
                throw new ConflictException(
                    "A meaning can only be assigned to a unit of the same vocabulary book.");
            }

            if (await _membershipRepository.ExistsAsync(unit.Id, meaning.Id, normalizedSection, normalizedEntryKind))
            {
                return 0;
            }

            await _membershipRepository.AddAsync(new VocabularyMeaningUnitModel
            {
                UnitId = unit.Id,
                MeaningId = meaning.Id,
                BookId = unit.BookId,
                Section = normalizedSection,
                EntryKind = normalizedEntryKind
            });
            await _unitOfWork.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>
    /// Removes one assignment — one place of one unit, one entry kind of that
    /// place. The meaning itself survives, including its other kinds of the
    /// same place, its other positions of the same unit, and its assignments
    /// to other units.
    /// </summary>
    public async Task RemoveMeaningAsync(string unitId, string meaningId, string? section, string? entryKind)
    {
        var normalizedUnitId = NormalizeRequired(unitId, "Unit ID is required.");
        var normalizedMeaningId = NormalizeRequired(meaningId, "Meaning ID is required.");
        var normalizedSection = NormalizeSection(section);
        var normalizedEntryKind = NormalizeEntryKind(entryKind);

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            if (!await _membershipRepository.ExistsAsync(normalizedUnitId, normalizedMeaningId, normalizedSection, normalizedEntryKind))
            {
                throw new ResourceNotFoundException(
                    "The meaning is not assigned to this vocabulary book unit.");
            }

            await _membershipRepository.DeleteAsync(normalizedUnitId, normalizedMeaningId, normalizedSection, normalizedEntryKind);
            await _unitOfWork.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>Moves exactly one classified or unclassified assignment inside one book.</summary>
    public async Task MovePositionAsync(string bookId, string meaningId,
        string fromUnitId, string? fromSection, string? fromEntryKind,
        string toUnitId, string? toSection, string? toEntryKind, CancellationToken cancellationToken = default)
    {
        var book = NormalizeRequired(bookId, "BookId is required.");
        var meaning = NormalizeRequired(meaningId, "Meaning ID is required.");
        var sourceUnit = NormalizeRequired(fromUnitId, "Source unit ID is required.");
        var targetUnit = NormalizeRequired(toUnitId, "Target unit ID is required.");
        var sourceSection = NormalizeSection(fromSection);
        var targetSection = NormalizeSection(toSection);
        var sourceKind = NormalizeEntryKind(fromEntryKind);
        var targetKind = NormalizeEntryKind(toEntryKind);
        cancellationToken.ThrowIfCancellationRequested();

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await VerifyPositionOwnershipAsync(book, meaning, sourceUnit, targetUnit);
            if (!await _membershipRepository.ExistsAsync(sourceUnit, meaning, sourceSection, sourceKind))
                throw new ResourceNotFoundException("Vocabulary meaning position was not found.");
            if (sourceUnit == targetUnit && sourceSection == targetSection && sourceKind == targetKind)
                return 0;
            if (await _membershipRepository.ExistsAsync(targetUnit, meaning, targetSection, targetKind))
                throw new ConflictException("Target vocabulary meaning position already exists.");
            // A phrase carries no part of speech. Positions that are already
            // phrases are existing data and keep moving as they are.
            if (targetKind == "phrase" && sourceKind != "phrase" &&
                !string.IsNullOrWhiteSpace((await _meaningRepository.GetByIdAsync(meaning))?.PartOfSpeech))
                throw new DomainValidationException("Phrase positions must not use a meaning with partOfSpeech.");

            await _membershipRepository.DeleteAsync(sourceUnit, meaning, sourceSection, sourceKind);
            await _membershipRepository.AddAsync(new VocabularyMeaningUnitModel
            {
                UnitId = targetUnit,
                MeaningId = meaning,
                BookId = book,
                Section = targetSection,
                EntryKind = targetKind
            });
            await _unitOfWork.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>Removes one exact assignment without cleaning its meaning or word.</summary>
    public async Task RemovePositionAsync(string bookId, string meaningId,
        string unitId, string? section, string? entryKind, CancellationToken cancellationToken = default)
    {
        var book = NormalizeRequired(bookId, "BookId is required.");
        var meaning = NormalizeRequired(meaningId, "Meaning ID is required.");
        var unit = NormalizeRequired(unitId, "Unit ID is required.");
        var normalizedSection = NormalizeSection(section);
        var normalizedKind = NormalizeEntryKind(entryKind);
        cancellationToken.ThrowIfCancellationRequested();

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await VerifyPositionOwnershipAsync(book, meaning, unit, unit);
            if (!await _membershipRepository.ExistsAsync(unit, meaning, normalizedSection, normalizedKind))
                throw new ResourceNotFoundException("Vocabulary meaning position was not found.");
            await _membershipRepository.DeleteAsync(unit, meaning, normalizedSection, normalizedKind);
            await _unitOfWork.SaveChangesAsync();
            return 0;
        });
    }

    private async Task VerifyPositionOwnershipAsync(string bookId, string meaningId,
        string sourceUnitId, string targetUnitId)
    {
        _ = await _bookRepository.GetByIdAsync(bookId)
            ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
        var meaning = await _meaningRepository.GetByIdAsync(meaningId);
        if (meaning?.BookId != bookId)
            throw new ResourceNotFoundException("Vocabulary meaning was not found.");
        var source = await _unitRepository.GetByIdAsync(sourceUnitId);
        if (source?.BookId != bookId)
            throw new ResourceNotFoundException("Vocabulary book unit was not found.");
        if (sourceUnitId != targetUnitId)
        {
            var target = await _unitRepository.GetByIdAsync(targetUnitId);
            if (target?.BookId != bookId)
                throw new ResourceNotFoundException("Vocabulary book unit was not found.");
        }
    }

    /// <summary>
    /// Null or blank is no section; anything else must be exactly <c>A</c> or
    /// <c>B</c> after trimming, with case significant.
    /// </summary>
    private static string? NormalizeSection(string? section)
    {
        var normalized = VocabularyMeaningUnitSections.NormalizeOrNull(section);
        if (!VocabularyMeaningUnitSections.IsValid(normalized))
        {
            throw new DomainValidationException("Section must be A or B.");
        }

        return normalized;
    }

    /// <summary>
    /// Null or blank is no entry kind; anything else must be exactly
    /// <c>word</c> or <c>phrase</c> after trimming, with case significant.
    /// </summary>
    private static string? NormalizeEntryKind(string? entryKind)
    {
        var normalized = VocabularyMeaningUnitEntryKinds.NormalizeOrNull(entryKind);
        if (!VocabularyMeaningUnitEntryKinds.IsValid(normalized))
        {
            throw new DomainValidationException("EntryKind must be word or phrase.");
        }

        return normalized;
    }

    private static string NormalizeRequired(string? value, string message)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new DomainValidationException(message);
        }

        return normalized;
    }
}
