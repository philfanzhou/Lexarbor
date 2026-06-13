using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ruoyu.Study.Vocabulary.Domain.Repositories;

namespace Ruoyu.Study.Vocabulary.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly VocabularyDbContext _dbContext;

    public UnitOfWork(VocabularyDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> SaveChangesAsync()
    {
        try
        {
            return await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new InvalidOperationException("违反了唯一性约束，可能尝试插入了重复的单词或数据。", ex);
        }
    }
}