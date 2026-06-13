using Grpc.Core;
using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Domain.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ruoyu.Study.Vocabulary.Service;

public class VocabularyServiceImpl : VocabularyGrpcService.VocabularyGrpcServiceBase
{
    private readonly VocabularyDomainService _vocabularyService;

    public VocabularyServiceImpl(VocabularyDomainService vocabularyService)
    {
        _vocabularyService = vocabularyService;
    }

    public override async Task<VocabularyDto> Get(GetDetailRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.WordId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ID is required"));
        if (string.IsNullOrWhiteSpace(request.BookId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Book ID is required"));

        try
        {
            var (word, meanings) = await _vocabularyService.GetDetailAsync(request.WordId, request.BookId);
            var dto = word.ToDto();
            dto.Meanings.AddRange(meanings.Select(m => m.ToDto()));
            return dto;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<VocabularyPageResult> Search(SearchRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Keyword))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Keyword is required"));

        try
        {
            var page = request.Page > 0 ? request.Page : 1;
            var size = request.Size > 0 ? request.Size : 20;

            var (items, totalCount) = await _vocabularyService.SearchAsync(request.Keyword, page, size);
            var totalPages = (int)Math.Ceiling(totalCount / (double)size);

            var result = new VocabularyPageResult
            {
                TotalPage = totalPages,
                TotalCount = totalCount
            };
            result.Items.AddRange(items.Select(e => e.ToDto()));
            return result;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<BoolResponse> AddOrUpdate(AddOrUpdateRequest request, ServerCallContext context)
    {
        if (request.Word == null || request.Meaning == null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Word and Meaning are required"));

        try
        {
            await _vocabularyService.AddOrUpdateAsync(request.Word.ToEntity(), request.Meaning.ToEntity());

            return new BoolResponse
            {
                Success = true,
                ErrorMessage = ""
            };
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<QuestionResponse> GetQuestion(GetQuestionRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.WordId) || string.IsNullOrWhiteSpace(request.BookId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "WordId and BookId are required"));

        try
        {
            var (word, meanings) = await _vocabularyService.GetDetailAsync(request.WordId, request.BookId);
            var correctMeaning = meanings.FirstOrDefault()
                                 ?? throw new RpcException(new Status(StatusCode.NotFound, "Meaning not found"));

            bool useChineseQuestion = request.ChineseToEnglish;
            if (!request.ChineseToEnglish)
            {
                useChineseQuestion = Guid.NewGuid().GetHashCode() % 2 == 0;
            }

            if (useChineseQuestion)
            {
                var distractorWords = await _vocabularyService.GetDistractorWordsAsync(request.WordId, request.BookId, 3);
                var options = new List<OptionDto>
                {
                    new OptionDto { Meaning = word.Word, IsCorrect = true }
                };
                options.AddRange(distractorWords.Select(w => new OptionDto { Meaning = w.Word, IsCorrect = false }));
                options = options.OrderBy(_ => Guid.NewGuid()).ToList();

                var response = new QuestionResponse
                {
                    Word = correctMeaning.Meaning
                };
                response.Options.AddRange(options);
                return response;
            }
            else
            {
                var distractorMeanings = await _vocabularyService.GetDistractorMeaningsAsync(request.WordId, request.BookId, 3);
                var options = new List<OptionDto>
                {
                    new OptionDto { Meaning = correctMeaning.Meaning, IsCorrect = true }
                };
                options.AddRange(distractorMeanings.Select(d => new OptionDto { Meaning = d.Meaning, IsCorrect = false }));
                options = options.OrderBy(_ => Guid.NewGuid()).ToList();

                var response = new QuestionResponse
                {
                    Word = word.Word
                };
                response.Options.AddRange(options);
                return response;
            }
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}