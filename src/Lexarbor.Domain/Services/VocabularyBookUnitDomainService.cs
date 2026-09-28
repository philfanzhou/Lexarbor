using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

/// <summary>
/// The domain rules for book units and meaning-to-unit assignments. This
/// service owns the invariants later management, import and query tasks build
/// on: unit numbers are unique per book and positive, one meaning may be
/// assigned to many units of its own book, and a repeated assignment changes
/// nothing.
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
    /// Assigns a meaning to a unit. Assigning an already-assigned meaning again
    /// returns without writing, so replaying an assignment is idempotent. The
    /// meaning must belong to the unit's book.
    /// </summary>
    public async Task AssignMeaningAsync(string unitId, string meaningId)
    {
        var normalizedUnitId = NormalizeRequired(unitId, "Unit ID is required.");
        var normalizedMeaningId = NormalizeRequired(meaningId, "Meaning ID is required.");

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

            if (await _membershipRepository.ExistsAsync(unit.Id, meaning.Id))
            {
                return 0;
            }

            await _membershipRepository.AddAsync(new VocabularyMeaningUnitModel
            {
                UnitId = unit.Id,
                MeaningId = meaning.Id,
                BookId = unit.BookId
            });
            await _unitOfWork.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>
    /// Removes one assignment. The meaning itself survives, including its
    /// assignments to other units.
    /// </summary>
    public async Task RemoveMeaningAsync(string unitId, string meaningId)
    {
        var normalizedUnitId = NormalizeRequired(unitId, "Unit ID is required.");
        var normalizedMeaningId = NormalizeRequired(meaningId, "Meaning ID is required.");

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            if (!await _membershipRepository.ExistsAsync(normalizedUnitId, normalizedMeaningId))
            {
                throw new ResourceNotFoundException(
                    "The meaning is not assigned to this vocabulary book unit.");
            }

            await _membershipRepository.DeleteAsync(normalizedUnitId, normalizedMeaningId);
            await _unitOfWork.SaveChangesAsync();
            return 0;
        });
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
