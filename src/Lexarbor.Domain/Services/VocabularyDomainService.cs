using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public class VocabularyDomainService
{
    /// <summary>
    /// The most entries one batch import may carry. It bounds how long a batch
    /// holds the process-wide write lock: 500 entries took about three seconds
    /// on a first import, during which every other administrative write waits.
    /// ADR-005 records the measurement.
    /// </summary>
    public const int MaxBatchEntries = 500;

    private readonly IVocabularyRepository _vocabularyRepository;
    private readonly IVocabularyBookRepository _bookRepository;
    private readonly IVocabularyMeaningRepository _meaningRepository;
    private readonly IVocabularyBookUnitRepository _unitRepository;
    private readonly IVocabularyMeaningUnitRepository _membershipRepository;
    private readonly IUnitOfWork _unitOfWork;

    public VocabularyDomainService(
        IVocabularyRepository vocabularyRepository,
        IVocabularyBookRepository bookRepository,
        IVocabularyMeaningRepository meaningRepository,
        IVocabularyBookUnitRepository unitRepository,
        IVocabularyMeaningUnitRepository membershipRepository,
        IUnitOfWork unitOfWork)
    {
        _vocabularyRepository = vocabularyRepository;
        _bookRepository = bookRepository;
        _meaningRepository = meaningRepository;
        _unitRepository = unitRepository;
        _membershipRepository = membershipRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<(VocabularyModel word, List<VocabularyMeaningModel> meanings)> GetDetailAsync(
        string vocabularyId,
        string bookId)
    {
        var book = await _bookRepository.GetByIdAsync(bookId)
                   ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
        if (!book.Status)
        {
            throw new BusinessRuleException("Vocabulary book is disabled.");
        }

        var word = await _vocabularyRepository.GetByIdAsync(vocabularyId)
                   ?? throw new ResourceNotFoundException("Vocabulary word was not found.");
        var meanings = await _meaningRepository.GetByBookAndVocabularyIdAsync(bookId, vocabularyId);
        // Ordinal, and with the definition as a tiebreak, so that the order is a
        // total one and is the same on every machine. The default string
        // comparer orders by the current culture, which made the sequence a
        // property of the host's locale rather than of the data.
        meanings = meanings
            .OrderBy(meaning => meaning.PartOfSpeech, StringComparer.Ordinal)
            .ThenBy(meaning => meaning.Meaning, StringComparer.Ordinal)
            .ToList();
        return (word, meanings);
    }

    public Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(
        string? keyword,
        int page,
        int size)
    {
        return _vocabularyRepository.SearchAsync(keyword, page, size);
    }

    public async Task<(VocabularyModel word, VocabularyMeaningModel meaning)> AddOrUpdateAsync(
        VocabularyModel vocabulary,
        VocabularyMeaningModel meaning)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var (word, storedMeaning, _) = await AddOrUpdateCoreAsync(vocabulary, meaning);
            return (word, storedMeaning);
        });
    }

    /// <summary>
    /// Returns why one batch entry would be rejected, or null when it is valid.
    /// The HTTP endpoint reports this per entry; <see cref="ImportBatchAsync"/>
    /// applies the same rule so that a direct caller cannot skip it. A section
    /// is valid only together with the entry's unit reference: a section
    /// without a unit names a place of nothing. An entry kind is valid only
    /// together with one too: the kind is a property of an assignment's
    /// position, so a kind without a unit classifies nothing.
    /// </summary>
    public static string? ValidateBatchEntry(
        VocabularyModel word,
        VocabularyMeaningModel meaning,
        string? unitId,
        string? section,
        string? entryKind)
    {
        var missingWord = string.IsNullOrWhiteSpace(word.Word);
        var missingMeaning = string.IsNullOrWhiteSpace(meaning.Meaning);
        if (missingWord || missingMeaning)
        {
            return (missingWord, missingMeaning) switch
            {
                (true, true) => "Word and meaning are required.",
                (true, false) => "Word is required.",
                _ => "Meaning is required."
            };
        }

        var normalizedSection = VocabularyMeaningUnitSections.NormalizeOrNull(section);
        if (!VocabularyMeaningUnitSections.IsValid(normalizedSection))
        {
            return "Section must be A or B.";
        }

        if (normalizedSection != null && string.IsNullOrWhiteSpace(unitId))
        {
            return "Section requires a unitId.";
        }

        var normalizedEntryKind = VocabularyMeaningUnitEntryKinds.NormalizeOrNull(entryKind);
        if (!VocabularyMeaningUnitEntryKinds.IsValid(normalizedEntryKind))
        {
            return "EntryKind must be word or phrase.";
        }

        if (normalizedEntryKind != null && string.IsNullOrWhiteSpace(unitId))
        {
            return "EntryKind requires a unitId.";
        }

        return null;
    }

    /// <summary>
    /// Imports every entry into one book in a single transaction, so either the
    /// whole batch is stored or none of it is. Entries are applied in order with
    /// the same normalization and matching as <see cref="AddOrUpdateAsync"/>, so
    /// resubmitting a batch creates nothing new. Whitespace-only optional fields
    /// count as absent and never clear a stored value.
    /// </summary>
    /// <remarks>
    /// Every check that needs no database runs before the write lock is taken.
    /// The book and the entries' unit references are checked inside the
    /// transaction, so a concurrent disable, delete or unit removal cannot land
    /// between the check and the writes. An entry carrying a <c>unitId</c> gets
    /// the meaning it resolves to — created, reused from an earlier entry of the
    /// same batch, or already stored — assigned to that unit; a <c>section</c>
    /// narrows the assignment to that section's place of the unit, and an
    /// <c>entryKind</c> to that kind of the place, so a meaning may be assigned
    /// twice to one unit under two sections and twice to one place under two
    /// kinds. A repeated assignment writes nothing, and the counts still refer
    /// to meanings only.
    /// </remarks>
    public async Task<VocabularyBatchImportResult> ImportBatchAsync(
        string bookId,
        IReadOnlyList<(VocabularyModel Word, VocabularyMeaningModel Meaning, string? UnitId, string? Section, string? EntryKind)> entries)
    {
        var normalizedBookId = NormalizeRequired(bookId, "Book ID is required.");
        if (entries.Count == 0)
        {
            throw new DomainValidationException("At least one entry is required.");
        }

        if (entries.Count > MaxBatchEntries)
        {
            throw new DomainValidationException(
                $"A batch can contain at most {MaxBatchEntries} entries.");
        }

        var normalizedEntries =
            new List<(VocabularyModel Word, VocabularyMeaningModel Meaning, string? UnitId, string? Section, string? EntryKind)>(
                entries.Count);
        foreach (var (word, meaning, unitId, section, entryKind) in entries)
        {
            var error = ValidateBatchEntry(word, meaning, unitId, section, entryKind);
            if (error != null)
            {
                throw new DomainValidationException(error);
            }

            // Entries only ever add to the named book, so identifiers from the
            // caller are dropped rather than trusted.
            word.Id = string.Empty;
            word.PhoneticUk = NullIfWhiteSpace(word.PhoneticUk);
            word.PhoneticUs = NullIfWhiteSpace(word.PhoneticUs);
            meaning.Id = string.Empty;
            meaning.BookId = normalizedBookId;
            meaning.PartOfSpeech = NullIfWhiteSpace(meaning.PartOfSpeech);
            meaning.Example = NullIfWhiteSpace(meaning.Example);
            normalizedEntries.Add((word, meaning, NullIfWhiteSpace(unitId), VocabularyMeaningUnitSections.NormalizeOrNull(section), VocabularyMeaningUnitEntryKinds.NormalizeOrNull(entryKind)));
        }

        return await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var book = await _bookRepository.GetByIdAsync(normalizedBookId)
                       ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
            if (!book.Status)
            {
                throw new BusinessRuleException("New meanings cannot be added to a disabled vocabulary book.");
            }

            // One read answers every entry's unit reference. Checked inside the
            // same transaction the memberships are written in: the serialized
            // write lock keeps a concurrent unit delete from landing between the
            // check and the write, and a unit removed by an outside writer is
            // still caught by the membership's foreign keys, which rolls the
            // whole batch back. A unit of another book is indistinguishable from
            // a missing one here, so neither leaks that the other book has it.
            var bookUnitIds = (await _unitRepository.GetByBookIdAsync(normalizedBookId))
                .Select(unit => unit.Id)
                .ToHashSet();
            List<(int Index, string Message)>? unitErrors = null;
            for (var index = 0; index < normalizedEntries.Count; index++)
            {
                var unitId = normalizedEntries[index].UnitId;
                if (unitId != null && !bookUnitIds.Contains(unitId))
                {
                    (unitErrors ??= new List<(int, string)>())
                        .Add((index, "Unit was not found in the requested vocabulary book."));
                }
            }

            if (unitErrors != null)
            {
                throw new BatchEntryValidationException(
                    unitErrors.Count == 1
                        ? "1 entry is invalid."
                        : $"{unitErrors.Count} entries are invalid.",
                    unitErrors);
            }

            var created = 0;
            foreach (var (word, meaning, unitId, section, entryKind) in normalizedEntries)
            {
                var (_, storedMeaning, meaningCreated) = await AddOrUpdateCoreAsync(word, meaning);
                if (meaningCreated)
                {
                    created++;
                }

                if (unitId != null)
                {
                    // The membership write shares the batch transaction, so a
                    // failure anywhere leaves no half-imported assignments. The
                    // existence check sees rows this same transaction saved, so
                    // a meaning reused by a later entry of the same batch is not
                    // assigned twice — and a section and an entry kind name a
                    // place of the unit, so A and B of one unit, and the word
                    // and phrase of one place, are idempotent positions.
                    if (!await _membershipRepository.ExistsAsync(unitId, storedMeaning.Id, section, entryKind))
                    {
                        await _membershipRepository.AddAsync(new VocabularyMeaningUnitModel
                        {
                            UnitId = unitId,
                            MeaningId = storedMeaning.Id,
                            BookId = normalizedBookId,
                            Section = section,
                            EntryKind = entryKind
                        });
                        await _unitOfWork.SaveChangesAsync();
                    }
                }
            }

            return new VocabularyBatchImportResult(entries.Count, created, entries.Count - created);
        });
    }

    /// <summary>
    /// The body of <see cref="AddOrUpdateAsync"/>, run inside a transaction the
    /// caller owns. It also reports whether the meaning was inserted rather than
    /// matched, which the batch import counts and the single path discards.
    /// </summary>
    private async Task<(VocabularyModel Word, VocabularyMeaningModel Meaning, bool MeaningCreated)>
        AddOrUpdateCoreAsync(VocabularyModel vocabulary, VocabularyMeaningModel meaning)
    {
        var normalizedWord = NormalizeWord(vocabulary.Word);
        var bookId = NormalizeRequired(meaning.BookId, "BookId is required.");
        var normalizedMeaning = NormalizeRequired(meaning.Meaning, "Meaning is required.");
        var normalizedPartOfSpeech = NormalizePartOfSpeech(meaning.PartOfSpeech);

        var book = await _bookRepository.GetByIdAsync(bookId)
                   ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
        var isNewMeaning = string.IsNullOrWhiteSpace(meaning.Id);
        if (isNewMeaning && !book.Status)
        {
            throw new BusinessRuleException("New meanings cannot be added to a disabled vocabulary book.");
        }

        var existingVocabulary = await ResolveVocabularyAsync(vocabulary, normalizedWord);
        var (existingMeaning, meaningCreated) = await ResolveMeaningAsync(
            meaning,
            existingVocabulary.Id,
            bookId,
            normalizedPartOfSpeech,
            normalizedMeaning);

        await _unitOfWork.SaveChangesAsync();
        return (existingVocabulary, existingMeaning, meaningCreated);
    }

    public async Task<VocabularyQuestionModel> CreateQuestionAsync(
        string wordId,
        string bookId,
        bool chineseToEnglish)
    {
        var (word, meanings) = await GetDetailAsync(wordId, bookId);
        if (meanings.Count == 0)
        {
            throw new ResourceNotFoundException(
                "Vocabulary meaning was not found in the requested book.");
        }

        // Drawn rather than taken from the front of the list. A word carries one
        // definition per part of speech in a book, and taking the first left
        // every definition but one unaskable: the request carries no way to name
        // a definition, so for a word with more than one sense the second and
        // later senses could never be the subject of a question in either
        // direction. Which one is asked is now a property of the draw, and the
        // exclusions below all key off the definition that was drawn.
        var correctMeaning = meanings[Random.Shared.Next(meanings.Count)];

        List<VocabularyQuestionOptionModel> options;
        string questionText;
        if (chineseToEnglish)
        {
            // correctMeaning is the stem, so any word that also carries it in
            // this book answers the question correctly and cannot be offered as
            // a wrong option. The English-to-Chinese branch below has always
            // excluded by meaning; this direction now matches it.
            var distractors = await _vocabularyRepository.GetRandomByBookExceptAsync(
                bookId,
                wordId,
                word.Word,
                correctMeaning.Meaning,
                3);
            questionText = correctMeaning.Meaning;
            options =
            [
                new VocabularyQuestionOptionModel { Text = word.Word, IsCorrect = true },
                .. distractors.Select(item =>
                    new VocabularyQuestionOptionModel { Text = item.Word, IsCorrect = false })
            ];
        }
        else
        {
            var distractors =
                await _meaningRepository.GetRandomDistinctVocabularyExceptAsync(
                    bookId,
                    wordId,
                    correctMeaning.Meaning,
                    3);
            questionText = word.Word;
            options =
            [
                new VocabularyQuestionOptionModel
                {
                    Text = correctMeaning.Meaning,
                    IsCorrect = true
                },
                .. distractors.Select(item =>
                    new VocabularyQuestionOptionModel
                    {
                        Text = item.Meaning,
                        IsCorrect = false
                    })
            ];
        }

        options = options
            .Where(option => !string.IsNullOrWhiteSpace(option.Text))
            .DistinctBy(option => option.Text, StringComparer.Ordinal)
            .ToList();
        if (options.Count != 4 || options.Count(option => option.IsCorrect) != 1)
        {
            throw new BusinessRuleException(
                "The vocabulary book does not contain enough distinct words to create a question.");
        }

        Shuffle(options);
        return new VocabularyQuestionModel
        {
            Word = questionText,
            Options = options
        };
    }

    private async Task<VocabularyModel> ResolveVocabularyAsync(
        VocabularyModel requested,
        string normalizedWord)
    {
        VocabularyModel? existing;
        if (!string.IsNullOrWhiteSpace(requested.Id))
        {
            existing = await _vocabularyRepository.GetByIdAsync(requested.Id)
                       ?? throw new ResourceNotFoundException("Vocabulary word was not found.");

            var wordWithSameNormalizedValue =
                await _vocabularyRepository.GetByNormalizedWordAsync(normalizedWord);
            if (wordWithSameNormalizedValue != null && wordWithSameNormalizedValue.Id != existing.Id)
            {
                throw new ConflictException("A vocabulary word with the same normalized value already exists.");
            }
        }
        else
        {
            existing = await _vocabularyRepository.GetByNormalizedWordAsync(normalizedWord);
            if (existing == null)
            {
                var now = DateTimeOffset.UtcNow;
                requested.Id = Guid.NewGuid().ToString();
                // The display spelling keeps the imported casing; equivalence is
                // the normalized key's job, not the stored word's. First
                // creation wins: a later import of the same normalized word with
                // different casing resolves to this row and leaves the spelling
                // alone, and only the administrator's explicit edit can correct
                // it while the normalized key is unchanged.
                requested.Word = requested.Word.Trim();
                requested.CreatedAt = now;
                requested.UpdatedAt = now;
                await _vocabularyRepository.AddAsync(requested);
                return requested;
            }
        }

        // The stored spelling is deliberately not rewritten to the imported
        // casing: an import is not an edit, and overwriting here would silently
        // discard the first creator's spelling (or re-lowercase it, as this
        // path used to do). Equivalence is decided by the normalized key
        // above; phonetics still merge because importing a word that carries
        // new phonetic information should store it.
        var changed = false;

        if (requested.PhoneticUk != null &&
            !string.Equals(existing.PhoneticUk, requested.PhoneticUk, StringComparison.Ordinal))
        {
            existing.PhoneticUk = requested.PhoneticUk;
            changed = true;
        }

        if (requested.PhoneticUs != null &&
            !string.Equals(existing.PhoneticUs, requested.PhoneticUs, StringComparison.Ordinal))
        {
            existing.PhoneticUs = requested.PhoneticUs;
            changed = true;
        }

        if (changed)
        {
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await _vocabularyRepository.UpdateAsync(existing);
        }

        return existing;
    }

    private async Task<(VocabularyMeaningModel Meaning, bool Created)> ResolveMeaningAsync(
        VocabularyMeaningModel requested,
        string vocabularyId,
        string bookId,
        string normalizedPartOfSpeech,
        string normalizedMeaning)
    {
        if (!string.IsNullOrWhiteSpace(requested.Id))
        {
            var existing = await _meaningRepository.GetByIdAsync(requested.Id)
                           ?? throw new ResourceNotFoundException("Vocabulary meaning was not found.");

            if (existing.VocabularyId != vocabularyId)
            {
                throw new ConflictException("Vocabulary meaning belongs to a different word.");
            }

            if (existing.BookId != bookId)
            {
                throw new ConflictException("Vocabulary meaning belongs to a different book.");
            }

            var equivalentMeaning = await _meaningRepository.GetEquivalentAsync(
                vocabularyId,
                bookId,
                normalizedPartOfSpeech,
                normalizedMeaning);
            if (equivalentMeaning != null && equivalentMeaning.Id != existing.Id)
            {
                throw new ConflictException(
                    "An equivalent vocabulary meaning already exists.");
            }

            existing.PartOfSpeech = normalizedPartOfSpeech;
            existing.Meaning = normalizedMeaning;
            existing.Example = requested.Example?.Trim();
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await _meaningRepository.UpdateAsync(existing);
            return (existing, false);
        }

        var equivalent = await _meaningRepository.GetEquivalentAsync(
            vocabularyId,
            bookId,
            normalizedPartOfSpeech,
            normalizedMeaning);
        if (equivalent != null)
        {
            var changed = false;
            if (!string.Equals(
                    equivalent.PartOfSpeech,
                    normalizedPartOfSpeech,
                    StringComparison.Ordinal))
            {
                equivalent.PartOfSpeech = normalizedPartOfSpeech;
                changed = true;
            }

            if (!string.Equals(
                    equivalent.Meaning,
                    normalizedMeaning,
                    StringComparison.Ordinal))
            {
                equivalent.Meaning = normalizedMeaning;
                changed = true;
            }

            if (requested.Example != null)
            {
                var normalizedExample = requested.Example.Trim();
                if (!string.Equals(
                        equivalent.Example,
                        normalizedExample,
                        StringComparison.Ordinal))
                {
                    equivalent.Example = normalizedExample;
                    changed = true;
                }
            }

            if (changed)
            {
                equivalent.UpdatedAt = DateTimeOffset.UtcNow;
                await _meaningRepository.UpdateAsync(equivalent);
            }

            return (equivalent, false);
        }

        var now = DateTimeOffset.UtcNow;
        requested.Id = Guid.NewGuid().ToString();
        requested.VocabularyId = vocabularyId;
        requested.BookId = bookId;
        requested.PartOfSpeech = normalizedPartOfSpeech;
        requested.Meaning = normalizedMeaning;
        requested.Example = requested.Example?.Trim();
        requested.CreatedAt = now;
        requested.UpdatedAt = now;
        await _meaningRepository.AddAsync(requested);
        return (requested, true);
    }

    private static string NormalizeWord(string word)
    {
        var normalized = word?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new DomainValidationException("Word is required.");
        }

        return normalized;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NormalizePartOfSpeech(string? partOfSpeech)
    {
        return partOfSpeech?.Trim().ToLowerInvariant() ?? string.Empty;
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

    private static void Shuffle<T>(IList<T> items)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }
}
