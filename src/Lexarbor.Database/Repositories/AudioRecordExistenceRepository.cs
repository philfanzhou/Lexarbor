using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class AudioRecordExistenceRepository(VocabularyDbContext context) : IAudioRecordExistenceRepository
{
    public async Task<bool> HasAnyAsync(CancellationToken cancellationToken) =>
        await context.AudioAssets.AsNoTracking().AnyAsync(cancellationToken)
        || await context.AudioUploadOperations.AsNoTracking().AnyAsync(cancellationToken)
        || await context.AudioInvalidations.AsNoTracking().AnyAsync(cancellationToken);
}
