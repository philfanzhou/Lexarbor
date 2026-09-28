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

    public async Task<Dictionary<string, int>> GetAssignmentCountsByBookIdAsync(string bookId)
    {
        // One grouped read over the book's memberships answers every unit at
        // once; counting per unit instead would turn a list endpoint into one
        // query per row. Distinct meanings, not positions: a meaning that
        // holds both Section A and Section B of one unit is still one meaning
        // of that unit.
        var counts = await _context.VocabularyMeaningUnits
            .AsNoTracking()
            .Where(membership => membership.BookId == bookId)
            .GroupBy(membership => membership.UnitId)
            .Select(group => new
            {
                UnitId = group.Key,
                Count = group.Select(membership => membership.MeaningId).Distinct().Count()
            })
            .ToListAsync();
        return counts.ToDictionary(item => item.UnitId, item => item.Count);
    }

    public async Task AddAsync(VocabularyBookUnitModel model)
    {
        var entity = model.Adapt<VocabularyBookUnitEntity>();
        await _context.VocabularyBookUnits.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyBookUnitModel model)
    {
        var entity = await _context.VocabularyBookUnits.FindAsync(model.Id);
        if (entity != null)
        {
            entity.Number = model.Number;
            entity.Title = model.Title;
            entity.UpdatedAt = model.UpdatedAt;
        }
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

    public Task<bool> ExistsAsync(string unitId, string meaningId, string? section)
    {
        var storedSection = ToStoredSection(section);
        return _context.VocabularyMeaningUnits
            .AsNoTracking()
            .AnyAsync(membership =>
                membership.UnitId == unitId && membership.MeaningId == meaningId && membership.Section == storedSection);
    }

    public async Task<List<VocabularyMeaningUnitModel>> GetByUnitIdAsync(string unitId)
    {
        var entities = await _context.VocabularyMeaningUnits
            .AsNoTracking()
            .Where(membership => membership.UnitId == unitId)
            .ToListAsync();
        return entities.ConvertAll(ToModel);
    }

    public async Task<List<VocabularyMeaningUnitModel>> GetByMeaningIdAsync(string meaningId)
    {
        var entities = await _context.VocabularyMeaningUnits
            .AsNoTracking()
            .Where(membership => membership.MeaningId == meaningId)
            .ToListAsync();
        return entities.ConvertAll(ToModel);
    }

    public async Task AddAsync(VocabularyMeaningUnitModel model)
    {
        await _context.VocabularyMeaningUnits.AddAsync(ToEntity(model));
    }

    public async Task DeleteAsync(string unitId, string meaningId, string? section)
    {
        var entity = await _context.VocabularyMeaningUnits.FindAsync(unitId, meaningId, ToStoredSection(section));
        if (entity != null)
        {
            _context.VocabularyMeaningUnits.Remove(entity);
        }
    }

    // The entity stores no section as the empty string, because SQLite treats
    // NULLs in a composite primary key as mutually unequal and the key is what
    // makes a repeated assignment a conflict. The model uses null for "no
    // section" so writers never need to know the sentinel; these two methods
    // are the only place that translates.
    private static string ToStoredSection(string? section)
    {
        return section ?? string.Empty;
    }

    private static VocabularyMeaningUnitEntity ToEntity(VocabularyMeaningUnitModel model) => new()
    {
        UnitId = model.UnitId,
        MeaningId = model.MeaningId,
        BookId = model.BookId,
        Section = ToStoredSection(model.Section)
    };

    private static VocabularyMeaningUnitModel ToModel(VocabularyMeaningUnitEntity entity) => new()
    {
        UnitId = entity.UnitId,
        MeaningId = entity.MeaningId,
        BookId = entity.BookId,
        Section = entity.Section == string.Empty ? null : entity.Section
    };
}
