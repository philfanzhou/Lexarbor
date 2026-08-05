using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public class VocabularyDomainService
{
    private readonly IVocabularyRepository _vocabularyRepository;
    private readonly IVocabularyBookRepository _bookRepository;
    private readonly IVocabularyMeaningRepository _meaningRepository;
    private readonly IUnitOfWork _unitOfWork;

    public VocabularyDomainService(
        IVocabularyRepository vocabularyRepository,
        IVocabularyBookRepository bookRepository,
        IVocabularyMeaningRepository meaningRepository,
        IUnitOfWork unitOfWork)
    {
        _vocabularyRepository = vocabularyRepository;
        _bookRepository = bookRepository;
        _meaningRepository = meaningRepository;
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
        meanings = meanings.OrderBy(meaning => meaning.PartOfSpeech).ToList();
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
            var existingMeaning = await ResolveMeaningAsync(
                meaning,
                existingVocabulary.Id,
                bookId,
                normalizedPartOfSpeech,
                normalizedMeaning);

            await _unitOfWork.SaveChangesAsync();
            return (existingVocabulary, existingMeaning);
        });
    }

    public async Task<VocabularyQuestionModel> CreateQuestionAsync(
        string wordId,
        string bookId,
        bool chineseToEnglish)
    {
        var (word, meanings) = await GetDetailAsync(wordId, bookId);
        var correctMeaning = meanings.FirstOrDefault()
                             ?? throw new ResourceNotFoundException(
                                 "Vocabulary meaning was not found in the requested book.");

        List<VocabularyQuestionOptionModel> options;
        string questionText;
        if (chineseToEnglish)
        {
            var distractors = await _vocabularyRepository.GetRandomByBookExceptAsync(
                bookId,
                wordId,
                word.Word,
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
                requested.Word = normalizedWord;
                requested.CreatedAt = now;
                requested.UpdatedAt = now;
                await _vocabularyRepository.AddAsync(requested);
                return requested;
            }
        }

        var changed = false;
        if (!string.Equals(existing.Word, normalizedWord, StringComparison.Ordinal))
        {
            existing.Word = normalizedWord;
            changed = true;
        }

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

    private async Task<VocabularyMeaningModel> ResolveMeaningAsync(
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
            return existing;
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

            return equivalent;
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
        return requested;
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
