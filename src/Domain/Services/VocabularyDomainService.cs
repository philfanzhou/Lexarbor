using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Repositories;

namespace Ruoyu.Study.Vocabulary.Domain.Services;

public class VocabularyDomainService
{
    private readonly IVocabularyRepository _vocabularyRepository;
    private readonly IVocabularyMeaningRepository _meaningRepository;
    private readonly IUnitOfWork _unitOfWork;

    public VocabularyDomainService(
        IVocabularyRepository vocabularyRepository,
        IVocabularyMeaningRepository meaningRepository,
        IUnitOfWork unitOfWork)
    {
        _vocabularyRepository = vocabularyRepository;
        _meaningRepository = meaningRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<(VocabularyModel word, List<VocabularyMeaningModel> meanings)> GetDetailAsync(string vocabularyId, string bookId)
    {
        var word = await _vocabularyRepository.GetByIdAsync(vocabularyId)
                   ?? throw new KeyNotFoundException("Word not found");
        var meanings = await _meaningRepository.GetByBookAndVocabularyIdAsync(bookId, vocabularyId);
        meanings = meanings.OrderBy(m => m.PartOfSpeech).ToList();
        return (word, meanings);
    }

    public async Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(string keyword, int page, int size)
    {
        var (items, totalCount) = await _vocabularyRepository.SearchAsync(keyword, page, size);
        return (items, totalCount);
    }

    public async Task<(VocabularyModel word, VocabularyMeaningModel meaning)> AddOrUpdateAsync(VocabularyModel vocabulary, VocabularyMeaningModel meaning)
    {
        var existingVoc = !string.IsNullOrWhiteSpace(vocabulary.Id)
            ? await _vocabularyRepository.GetByIdAsync(vocabulary.Id)
            : await _vocabularyRepository.GetByWordAsync(vocabulary.Word);

        if (existingVoc == null)
        {
            vocabulary.Id = Guid.NewGuid().ToString();
            vocabulary.CreatedAt = DateTimeOffset.UtcNow;
            vocabulary.UpdatedAt = vocabulary.CreatedAt;
            await _vocabularyRepository.AddAsync(vocabulary);
            existingVoc = vocabulary;
        }
        else
        {
            bool updated = false;
            if (!string.IsNullOrWhiteSpace(vocabulary.Word) && vocabulary.Word != existingVoc.Word)
            {
                var check = await _vocabularyRepository.GetByWordAsync(vocabulary.Word);
                if (check != null && check.Id != existingVoc.Id)
                    throw new InvalidOperationException($"单词 '{vocabulary.Word}' 已存在。");
                existingVoc.Word = vocabulary.Word;
                updated = true;
            }
            if (!string.IsNullOrWhiteSpace(vocabulary.Phonetic) && vocabulary.Phonetic != existingVoc.Phonetic)
            {
                existingVoc.Phonetic = vocabulary.Phonetic;
                updated = true;
            }
            if (updated)
            {
                existingVoc.UpdatedAt = DateTimeOffset.UtcNow;
                await _vocabularyRepository.UpdateAsync(existingVoc);
            }
        }

        var existingMeaning = string.IsNullOrWhiteSpace(meaning.Id) ? null : await _meaningRepository.GetByIdAsync(meaning.Id);
        if (existingMeaning == null)
        {
            meaning.Id = Guid.NewGuid().ToString();
            meaning.VocabularyId = existingVoc.Id;
            meaning.CreatedAt = DateTimeOffset.UtcNow;
            meaning.UpdatedAt = meaning.CreatedAt;
            await _meaningRepository.AddAsync(meaning);
        }
        else
        {
            existingMeaning.PartOfSpeech = meaning.PartOfSpeech;
            existingMeaning.Meaning = meaning.Meaning;
            existingMeaning.Example = meaning.Example;
            existingMeaning.UpdatedAt = DateTimeOffset.UtcNow;
            await _meaningRepository.UpdateAsync(existingMeaning);
            meaning = existingMeaning;
        }

        await _unitOfWork.SaveChangesAsync();
        return (existingVoc, meaning);
    }

    public async Task<List<VocabularyMeaningModel>> GetDistractorMeaningsAsync(string wordId, string bookId, int count = 3)
    {
        return await _meaningRepository.GetRandomExceptAsync(wordId, bookId, count);
    }

    public async Task<List<VocabularyModel>> GetDistractorWordsAsync(string wordId, string bookId, int count = 3)
    {
        return await _vocabularyRepository.GetRandomExceptAsync(wordId, count);
    }
}