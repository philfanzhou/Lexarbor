using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class VocabularyRelationService(IVocabularyRelationRepository repository,
    IUnitOfWork unitOfWork, TimeProvider time)
{
    public const int MaxBatchSize = 100;

    public async Task<VocabularyRelationModel> GetAsync(string id, CancellationToken cancellationToken = default) =>
        await repository.GetAsync(Required(id, "Relation ID"), cancellationToken)
        ?? throw new ResourceNotFoundException("Vocabulary relation was not found.");

    public async Task<VocabularyRelationPage> ListAsync(string? wordId, VocabularyRelationQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Direction is not ("both" or "incoming" or "outgoing")
            || (query.Type != null && !VocabularyRelationTypes.IsValid(query.Type))
            || (query.Status != null && query.Status is not ("confirmed" or "needs_review"))
            || query.Page is < 1 or > 1_000_000 || query.Size is < 1 or > 100)
            throw new DomainValidationException("The relation query is invalid.");
        if (wordId != null)
        {
            wordId = Required(wordId, "Word ID");
            if (!await repository.WordExistsAsync(wordId, cancellationToken))
                throw new ResourceNotFoundException("Vocabulary word was not found.");
        }
        return await repository.ListAsync(wordId, query, cancellationToken);
    }

    public Task<VocabularyRelationCreation> CreateAsync(VocabularyRelationInput input,
        CancellationToken cancellationToken = default)
    {
        input = Normalize(input);
        cancellationToken.ThrowIfCancellationRequested();
        return unitOfWork.ExecuteInTransactionAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateInsideAsync(input, "manual", null);
        });
    }

    public Task<VocabularyRelationBatchResult> CreateBatchAsync(IReadOnlyList<VocabularyRelationInput?> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count is < 1 or > MaxBatchSize)
            throw new DomainValidationException($"A relation batch must contain 1 to {MaxBatchSize} items.");
        var normalized = inputs.Select((input, index) =>
        {
            try { return Normalize(input); }
            catch (DomainValidationException error)
            { throw new DomainValidationException($"Relation at index {index}: {error.Message}"); }
        }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchId = Guid.NewGuid().ToString();
            var items = new List<VocabularyRelationModel>();
            var created = 0;
            for (var index = 0; index < normalized.Length; index++)
            {
                try
                {
                    var result = await CreateInsideAsync(normalized[index], "import", batchId);
                    items.Add(result.Relation);
                    if (result.Created) created++;
                }
                catch (ResourceNotFoundException error)
                { throw new ResourceNotFoundException($"Relation at index {index}: {error.Message}"); }
                catch (ConflictException error)
                { throw new ConflictException($"Relation at index {index}: {error.Message}"); }
            }
            return new VocabularyRelationBatchResult(items, created, items.Count - created);
        });
    }

    public Task<VocabularyRelationModel> ReplaceAsync(string id, VocabularyRelationInput input, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        id = Required(id, "Relation ID");
        input = Normalize(input);
        ValidateVersion(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await GetAsync(id);
            if (current.Version != expectedVersion) throw VersionConflict();
            await RequireWordsAsync(input);
            var equivalent = await repository.FindAsync(input.SourceWordId, input.TargetWordId, input.Type, CancellationToken.None);
            if (equivalent != null && equivalent.Id != id)
                throw new ConflictException("An equivalent vocabulary relation already exists.");
            var changed = current.SourceWordId != input.SourceWordId || current.TargetWordId != input.TargetWordId
                || current.Type != input.Type || current.UsageNote != input.UsageNote;
            var now = Math.Max(time.GetUtcNow().ToUnixTimeMilliseconds(), current.UpdatedAtUnixMs);
            var updated = current with
            {
                SourceWordId = input.SourceWordId,
                TargetWordId = input.TargetWordId,
                Type = input.Type,
                UsageNote = input.UsageNote,
                Status = input.Confirm ? "confirmed" : changed ? "needs_review" : current.Status,
                ReviewReason = input.Confirm ? null : changed ? "relation_changed" : current.ReviewReason,
                LastConfirmedAtUnixMs = input.Confirm ? now : current.LastConfirmedAtUnixMs,
                UpdatedAtUnixMs = now,
                Version = checked(current.Version + 1)
            };
            if (!await repository.ReplaceAsync(updated, expectedVersion, CancellationToken.None)) throw VersionConflict();
            return updated;
        });
    }

    public Task DeleteAsync(string id, long expectedVersion, CancellationToken cancellationToken = default)
    {
        id = Required(id, "Relation ID");
        ValidateVersion(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await repository.GetAsync(id, CancellationToken.None);
            if (current == null) return 0;
            if (current.Version != expectedVersion || !await repository.DeleteAsync(id, expectedVersion, CancellationToken.None))
                throw VersionConflict();
            return 0;
        });
    }

    private async Task<VocabularyRelationCreation> CreateInsideAsync(VocabularyRelationInput input, string origin, string? reference)
    {
        await RequireWordsAsync(input);
        var current = await repository.FindAsync(input.SourceWordId, input.TargetWordId, input.Type, CancellationToken.None);
        if (current != null)
        {
            if (current.UsageNote != input.UsageNote)
                throw new ConflictException("An equivalent vocabulary relation has different content; update it explicitly.");
            // Replays never confirm an existing relation or overwrite its initial provenance.
            return new(current, false);
        }
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var relation = new VocabularyRelationModel(Guid.NewGuid().ToString(), input.SourceWordId, input.TargetWordId,
            input.Type, input.UsageNote, input.Confirm ? "confirmed" : "needs_review",
            input.Confirm ? null : "new_relation", origin, reference, 1, now, now, input.Confirm ? now : null);
        await repository.AddAsync(relation, CancellationToken.None);
        await unitOfWork.SaveChangesAsync();
        return new(relation, true);
    }

    private async Task RequireWordsAsync(VocabularyRelationInput input)
    {
        if (!await repository.WordExistsAsync(input.SourceWordId, CancellationToken.None)
            || !await repository.WordExistsAsync(input.TargetWordId, CancellationToken.None))
            throw new ResourceNotFoundException("Source or target vocabulary word was not found.");
    }

    private static VocabularyRelationInput Normalize(VocabularyRelationInput? input)
    {
        if (input == null) throw new DomainValidationException("A vocabulary relation is required.");
        var type = Required(input.Type, "Type");
        if (!VocabularyRelationTypes.IsValid(type)) throw new DomainValidationException("The relation type is invalid.");
        var note = string.IsNullOrWhiteSpace(input.UsageNote) ? null : input.UsageNote.Trim();
        if (note?.Length > 2000 || note?.Contains('\0') == true)
            throw new DomainValidationException("Usage note must contain at most 2000 characters and no null characters.");
        return input with
        {
            SourceWordId = Required(input.SourceWordId, "Source word ID"),
            TargetWordId = Required(input.TargetWordId, "Target word ID"),
            Type = type,
            UsageNote = note
        };
    }

    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value) || value.Contains('\0')
        ? throw new DomainValidationException($"{name} is required and must not contain null characters.") : value.Trim();
    private static void ValidateVersion(long version)
    {
        if (version < 1 || version == long.MaxValue)
            throw new DomainValidationException("Expected version must be a positive, incrementable integer.");
    }
    private static ConflictException VersionConflict() => new("The vocabulary relation has changed; reload it before editing.");
}
