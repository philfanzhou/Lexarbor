using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Domain.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Ruoyu.Study.Vocabulary.Service;

public class VocabularyBookServiceImpl : VocabularyBookGrpcService.VocabularyBookGrpcServiceBase
{
    private readonly VocabularyBookDomainService _bookService;

    public VocabularyBookServiceImpl(VocabularyBookDomainService bookService)
    {
        _bookService = bookService;
    }

    public override async Task<BoolResponse> Add(VocabularyBookDto request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.BookName))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "BookName is required"));

        try
        {
            var entity = request.ToEntity();
            await _bookService.AddOrUpdateAsync(entity);

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

    public override async Task<BoolResponse> Update(VocabularyBookDto request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Id is required"));

        try
        {
            var entity = request.ToEntity();
            await _bookService.AddOrUpdateAsync(entity);

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

    public override async Task<VocabularyBookDto> Get(IdRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Id is required"));

        try
        {
            var entity = await _bookService.GetAsync(request.Id)
                          ?? throw new RpcException(new Status(StatusCode.NotFound, "Book not found"));
            return entity.ToDto();
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<VocabularyBookPageResult> Search(SearchBookRequest request, ServerCallContext context)
    {
        try
        {
            var page = request.Page > 0 ? request.Page : 1;
            var size = request.Size > 0 ? request.Size : 20;

            var (entities, totalCount) = await _bookService.SearchAsync(request.Keyword ?? string.Empty, page, size);
            var totalPages = (int)Math.Ceiling(totalCount / (double)size);

            var result = new VocabularyBookPageResult
            {
                TotalPage = totalPages,
                TotalCount = totalCount
            };
            result.Items.AddRange(entities.Select(e => e.ToDto()));
            return result;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<VocabularyBookDtoList> GetByCategory(GetByCategoryRequest request, ServerCallContext context)
    {
        try
        {
            var entities = await _bookService.GetByCategoryAsync(request.Category, request.HasGrade ? request.Grade : null);

            var result = new VocabularyBookDtoList();
            result.Books.AddRange(entities.Select(e => e.ToDto()));
            return result;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<VocabularyBookDtoList> GetAll(Empty request, ServerCallContext context)
    {
        try
        {
            var entities = await _bookService.GetAllAsync();

            var result = new VocabularyBookDtoList();
            result.Books.AddRange(entities.Select(e => e.ToDto()));
            return result;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<StringList> GetAllCategories(Empty _, ServerCallContext __)
    {
        var list = await _bookService.GetAllCategoriesAsync();
        var res = new StringList();
        res.Items.AddRange(list);
        return res;
    }

    public override async Task<StringList> GetAllEducationLevels(Empty _, ServerCallContext __)
    {
        var list = await _bookService.GetAllEducationLevelsAsync();
        var res = new StringList();
        res.Items.AddRange(list);
        return res;
    }

    public override async Task<StringList> GetAllGrades(Empty _, ServerCallContext __)
    {
        var list = await _bookService.GetAllGradesAsync();
        var res = new StringList();
        res.Items.AddRange(list);
        return res;
    }

    public override async Task<StringList> GetGradesByEducationLevel(StringRequest request, ServerCallContext __)
    {
        var list = await _bookService.GetGradesByEducationLevelAsync(request.Value);
        var res = new StringList();
        res.Items.AddRange(list);
        return res;
    }

    public override async Task<VocabularyDtoList> GetBookWords(IdRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "BookId is required"));

        try
        {
            var words = await _bookService.GetWordsAsync(request.Id);
            var result = new VocabularyDtoList();
            result.Words.AddRange(words.Select(e => e.ToDto()));
            return result;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<BoolResponse> Delete(IdRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Id is required"));

        try
        {
            await _bookService.DeleteAsync(request.Id);

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
}