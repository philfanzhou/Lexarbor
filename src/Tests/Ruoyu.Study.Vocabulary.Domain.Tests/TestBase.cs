using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Vocabulary.Database;
using Ruoyu.Study.Vocabulary.Database.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Repositories;

namespace Ruoyu.Study.Vocabulary.Domain.Tests;

public class TestBase : IDisposable
{
    private readonly SqliteConnection _connection;
    protected readonly VocabularyDbContext _dbContext;
    protected readonly IVocabularyRepository _vocabularyRepository;
    protected readonly IVocabularyBookRepository _bookRepository;
    protected readonly IVocabularyMeaningRepository _meaningRepository;
    protected readonly IUnitOfWork _unitOfWork;

    public TestBase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new VocabularyDbContext(options);
        _dbContext.Database.EnsureCreated();

        _vocabularyRepository = new VocabularyRepository(_dbContext);
        _bookRepository = new VocabularyBookRepository(_dbContext);
        _meaningRepository = new VocabularyMeaningRepository(_dbContext);
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
        await _unitOfWork.SaveChangesAsync();
        return book;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }
}
