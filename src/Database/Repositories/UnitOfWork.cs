using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ruoyu.Study.Vocabulary.Domain.Exceptions;
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
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation
            })
        {
            throw new ConflictException(
                "The requested vocabulary data conflicts with existing data.",
                exception);
        }
    }
}
