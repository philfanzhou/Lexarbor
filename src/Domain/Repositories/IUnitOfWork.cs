using System.Threading.Tasks;

namespace Ruoyu.Study.Vocabulary.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}