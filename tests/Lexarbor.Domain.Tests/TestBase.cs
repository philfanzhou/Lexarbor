using System;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class TestBase : IDisposable
{
    protected string ConnectionString => _database.ConnectionString;

    private readonly PostgreSqlTestDatabase _database;
    protected readonly VocabularyDbContext _dbContext;
    protected readonly IVocabularyRepository _vocabularyRepository;
    protected readonly IVocabularyBookRepository _bookRepository;
    protected readonly IVocabularyMeaningRepository _meaningRepository;
    protected readonly IVocabularyBookUnitRepository _bookUnitRepository;
    protected readonly IVocabularyMeaningUnitRepository _meaningUnitRepository;
    protected readonly IUnitOfWork _unitOfWork;

    public TestBase()
    {
        _database = new PostgreSqlTestDatabase();
        _dbContext = _database.Open();

        _vocabularyRepository = new VocabularyRepository(_dbContext);
        _bookRepository = new VocabularyBookRepository(_dbContext);
        _meaningRepository = new VocabularyMeaningRepository(_dbContext);
        _bookUnitRepository = new VocabularyBookUnitRepository(_dbContext);
        _meaningUnitRepository = new VocabularyMeaningUnitRepository(_dbContext);
        _unitOfWork = new UnitOfWork(_dbContext);
    }

    protected async Task<VocabularyBookModel> CreateBookAsync(bool status = true, string? id = null)
    {
        var now = DateTimeOffset.UtcNow;
        var book = new VocabularyBookModel
        {
            Id = id ?? Guid.NewGuid().ToString(),
            BookName = $"Book-{Guid.NewGuid():N}",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _bookRepository.AddAsync(book);
        await _unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
        return book;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _database.Dispose();
    }
}
