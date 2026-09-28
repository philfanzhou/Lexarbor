using System.Threading.Tasks;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public class VocabularyBookUnitRepository : IVocabularyBookUnitRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyBookUnitRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<VocabularyBookUnitModel?> GetByIdAsync(string id)
    {
        var entity = await _context.VocabularyBookUnits
            .AsNoTracking()
            .SingleOrDefaultAsync(unit => unit.Id == id);
        return entity?.Adapt<VocabularyBookUnitModel>();
    }

    public async Task<List<VocabularyBookUnitModel>> GetByBookIdAsync(string bookId)
    {
        var entities = await _context.VocabularyBookUnits
            .AsNoTracking()
            .Where(unit => unit.BookId == bookId)
            .OrderBy(unit => unit.Number)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBookUnitModel>>();
    }

    public async Task AddAsync(VocabularyBookUnitModel model)
    {
        var entity = model.Adapt<VocabularyBookUnitEntity>();
        await _context.VocabularyBookUnits.AddAsync(entity);
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _context.VocabularyBookUnits.FindAsync(id);
        if (entity != null)
        {
            _context.VocabularyBookUnits.Remove(entity);
        }
    }
}

public class VocabularyMeaningUnitRepository : IVocabularyMeaningUnitRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyMeaningUnitRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public Task<bool> ExistsAsync(string unitId, string meaningId)
    {
        return _context.VocabularyMeaningUnits
            .AsNoTracking()
            .AnyAsync(membership => membership.UnitId == unitId && membership.MeaningId == meaningId);
    }

    public async Task<List<VocabularyMeaningUnitModel>> GetByUnitIdAsync(string unitId)
    {
        var entities = await _context.VocabularyMeaningUnits
            .AsNoTracking()
            .Where(membership => membership.UnitId == unitId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningUnitModel>>();
    }

    public async Task<List<VocabularyMeaningUnitModel>> GetByMeaningIdAsync(string meaningId)
    {
        var entities = await _context.VocabularyMeaningUnits
            .AsNoTracking()
            .Where(membership => membership.MeaningId == meaningId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningUnitModel>>();
    }

    public async Task AddAsync(VocabularyMeaningUnitModel model)
    {
        var entity = model.Adapt<VocabularyMeaningUnitEntity>();
        await _context.VocabularyMeaningUnits.AddAsync(entity);
    }

    public async Task DeleteAsync(string unitId, string meaningId)
    {
        var entity = await _context.VocabularyMeaningUnits.FindAsync(unitId, meaningId);
        if (entity != null)
        {
            _context.VocabularyMeaningUnits.Remove(entity);
        }
    }
}
