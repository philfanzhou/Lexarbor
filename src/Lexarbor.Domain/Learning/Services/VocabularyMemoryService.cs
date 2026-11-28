using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyMemoryService
{
    private readonly IMemoryCardRepository _cardRepository;
    private readonly IMemoryReviewLogRepository _logRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly FsrsScheduler _scheduler;
    private readonly VocabularyFsrsOptions _options;

    public VocabularyMemoryService(
        IMemoryCardRepository cardRepository,
        IMemoryReviewLogRepository logRepository,
        IUnitOfWork unitOfWork,
        IOptions<VocabularyFsrsOptions> options)
    {
        _cardRepository = cardRepository;
        _logRepository = logRepository;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _scheduler = new FsrsScheduler(_options);
    }
    /// <summary>Applies a rating; <c>Created</c> is true when this call created the card.</summary>
    public async Task<(MemoryCard Card, bool Created)> RecordAsync(
        Guid studentId,
        string wordKey,
        string meaningKey,
        ReviewRating rating,
        DateTimeOffset reviewedAt,
        Guid? planId = null,
        CancellationToken cancellationToken = default)
    {
        if (rating == ReviewRating.Easy)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "The Easy rating is not accepted by the vocabulary memory domain.");
        }

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await _cardRepository.FindAsync(studentId, wordKey, meaningKey, cancellationToken).ConfigureAwait(false);
            var input = existing ?? new MemoryCard
            {
                StudentId = studentId,
                WordKey = wordKey,
                MeaningKey = meaningKey,
                State = MemoryCardState.New,
                DueAt = reviewedAt,
                FirstLearnedAt = reviewedAt,
                FirstLearnedPlanId = planId,
            };

            var (card, log) = _scheduler.Schedule(input, rating, reviewedAt);
            if (existing is null)
            {
                await _cardRepository.AddAsync(card, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _cardRepository.UpdateAsync(card, cancellationToken).ConfigureAwait(false);
            }

            await _logRepository.AddAsync(log, cancellationToken).ConfigureAwait(false);

            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (card, existing is null);
            }
            catch (MemoryCardConflictException) when (attempt == 0)
            {

                _unitOfWork.DiscardChanges();
            }
        }
    }

    public Task<List<MemoryCard>> GetDueAsync(
        Guid studentId,
        DateTimeOffset now,
        IReadOnlyCollection<CardKey>? keys = null,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        _cardRepository.GetDueAsync(studentId, now, keys, limit, cancellationToken);
    public MemoryStage GetStage(MemoryCard? card)
    {
        if (card is null) return MemoryStage.New;
        var stability = card.Stability ?? 0;
        if (stability < _options.FamiliarThresholdDays) return MemoryStage.Learning;
        if (stability < _options.MasteredThresholdDays) return MemoryStage.Familiar;
        return MemoryStage.Mastered;
    }
}
