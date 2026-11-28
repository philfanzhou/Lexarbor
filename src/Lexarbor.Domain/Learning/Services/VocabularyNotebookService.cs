using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyNotebookService : IVocabularyNotebookSource
{
    private const int LookupChunkSize = 100;

    private readonly TimeProvider _clock;
    private readonly ILearnerDirectory _studentRepository;
    private readonly IVocabNotebookRepository _notebookRepository;
    private readonly IMemoryCardRepository _cardRepository;
    private readonly IVocabularyNotebookCatalog _catalog;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<VocabularyNotebookService>? _logger;

    public VocabularyNotebookService(
        ILearnerDirectory studentRepository,
        IVocabNotebookRepository notebookRepository,
        IMemoryCardRepository cardRepository,
        IVocabularyNotebookCatalog catalog,
        IUnitOfWork unitOfWork,
        TimeProvider clock,
        ILogger<VocabularyNotebookService>? logger = null)
    {
        _studentRepository = studentRepository;
        _notebookRepository = notebookRepository;
        _cardRepository = cardRepository;
        _catalog = catalog;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _clock = clock;
    }

    public async Task<IReadOnlyList<VocabNotebookEntry>> ListAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        await RequireStudentAsync(studentId, cancellationToken).ConfigureAwait(false);
        await TrySyncPendingAsync(studentId, cancellationToken).ConfigureAwait(false);
        var items = await _notebookRepository.ListAsync(studentId, cancellationToken).ConfigureAwait(false);
        return await EntriesAsync(studentId, items, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VocabNotebookEntry>> EntriesAsync(Guid studentId, IReadOnlyList<VocabNotebookItem> items,
        CancellationToken cancellationToken)
    {
        var learned = items.Count == 0
            ? []
            : await _cardRepository.FindExistingKeysAsync(studentId, items.Select(item => item.Key).ToList(), cancellationToken).ConfigureAwait(false);
        return items
            .OrderByDescending(item => item.AddedAt)
            .ThenBy(item => item.Id)
            .Select(item => new VocabNotebookEntry(item, learned.Contains(item.Key)))
            .ToList();
    }

    private DateTimeOffset RecentStart()
    {
        var beijingOffset = TimeSpan.FromHours(8);
        var midnight = _clock.GetUtcNow().ToOffset(beijingOffset).Date.AddDays(-6);
        return new DateTimeOffset(midnight, beijingOffset).ToUniversalTime();
    }

    public async Task<VocabNotebookSummary> SummaryAsync(Guid studentId, CancellationToken cancellationToken)
    {
        await RequireStudentAsync(studentId, cancellationToken).ConfigureAwait(false);
        await TrySyncPendingAsync(studentId, cancellationToken).ConfigureAwait(false);
        return await _notebookRepository.SummaryAsync(studentId, RecentStart(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<VocabNotebookPage<VocabNotebookEntry>> QueryAsync(Guid studentId, VocabNotebookQuery filters,
        CancellationToken cancellationToken)
    {
        await RequireStudentAsync(studentId, cancellationToken).ConfigureAwait(false);
        await TrySyncPendingAsync(studentId, cancellationToken).ConfigureAwait(false);
        return await _notebookRepository.InRepeatableReadAsync(async () =>
        {
            var page = await _notebookRepository.QueryAsync(studentId, filters, RecentStart(), cancellationToken).ConfigureAwait(false);
            var entries = await EntriesAsync(studentId, page.Items, cancellationToken).ConfigureAwait(false);
            return new VocabNotebookPage<VocabNotebookEntry>(page.Total, page.Page, entries);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CardKey>> MembershipAsync(Guid studentId, IReadOnlyCollection<CardKey> keys,
        CancellationToken cancellationToken)
    {
        await RequireStudentAsync(studentId, cancellationToken).ConfigureAwait(false);
        return keys.Count == 0 ? [] : await _notebookRepository.MembershipAsync(studentId, keys, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<VocabCatalogMeaning>> SearchAsync(string? keyword, CancellationToken cancellationToken = default)
    {
        var text = Required(keyword, VocabNotebook.MaxWordLength);
        return _catalog.SearchAsync(text, cancellationToken);
    }

    public async Task<VocabNotebookEntry> AddExistingAsync(
        Guid studentId, VocabNotebookActor actor, string? word, string? meaningId, CancellationToken cancellationToken = default)
    {
        var spelling = Required(word, VocabNotebook.MaxWordLength);
        var id = Required(meaningId, 128);
        await RequireStudentAsync(studentId, cancellationToken).ConfigureAwait(false);
        var matches = await _catalog.SearchAsync(spelling, cancellationToken).ConfigureAwait(false);
        var meaning = matches.FirstOrDefault(match => match.MeaningId == id)
            ?? throw new VocabNotebookMeaningNotFoundException();
        var item = NewItem(studentId, actor, meaning.WordKey, meaning.MeaningKey, meaning.Word, meaning.PartOfSpeech, meaning.Meaning);
        Locate(item, meaning.BookId, meaning.WordId, meaning.MeaningId);
        return await UpsertAsync(item, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> AddFamilyAsync(Guid studentId, VocabNotebookActor actor, VocabCatalogMeaning meaning,
        VocabNotebookFamilySource source, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _notebookRepository.InStudentWriteAsync(studentId, async () =>
                {
                    var item = NewItem(studentId, actor, meaning.WordKey, meaning.MeaningKey, meaning.Word, meaning.PartOfSpeech, meaning.Meaning);
                    Locate(item, meaning.BookId, meaning.WordId, meaning.MeaningId);
                    var existing = await _notebookRepository.FindByKeyAsync(studentId, item.Key, cancellationToken).ConfigureAwait(false);
                    var addsSource = existing?.CanAttachFamily(source) ?? true;
                    item.AttachFamily(source);
                    item.FamilyOnly = true;
                    await MergeAsync(item, cancellationToken, existing).ConfigureAwait(false);
                    await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return existing is null ? "added" : addsSource ? "linked" : "in-notebook";
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (VocabNotebookWriteConflictException) when (attempt < 2)
            {
                // A manual notebook write can race the family lock; reread and merge its committed row.
            }
        }
    }
    public async Task<VocabNotebookEntry> SubmitAsync(
        Guid studentId, VocabNotebookActor actor, string? word, string? partOfSpeech, string? meaning,
        CancellationToken cancellationToken = default)
    {
        var spelling = Required(word, VocabNotebook.MaxWordLength);
        var definition = Required(meaning, VocabNotebook.MaxMeaningLength);
        var pos = Optional(partOfSpeech, VocabNotebook.MaxPartOfSpeechLength);
        await RequireStudentAsync(studentId, cancellationToken).ConfigureAwait(false);
        var submission = await _catalog.SubmitAsync(spelling, pos, definition, cancellationToken).ConfigureAwait(false);
        if (submission.Status == VocabSubmissionStatus.Rejected)
        {
            // Lexarbor answers a new pending submission for a rejected definition; a rejected answer is not expected.
            throw new VocabCatalogUnavailableException();
        }

        var item = submission.Status == VocabSubmissionStatus.Approved
            ? ApprovedItem(studentId, actor, submission)
            : PendingItem(studentId, actor, submission);
        return await UpsertAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Edits a rejected item's word, part of speech and meaning and submits it to Lexarbor again. Either actor may
    /// resubmit whoever added the item, and the item keeps who added it and when. Lexarbor answers a new pending
    /// submission (or an equivalent pending or kept-approved one), so the item becomes pending, or ready when approved
    /// at once; the review comment is cleared. The same key is updated in place; a changed key removes the rejected
    /// item and merges into the new key by the usual rules. Lexarbor is called before the write, and the write
    /// re-reads the item in one transaction: an item that is gone, no longer rejected or changed concurrently answers
    /// VocabNotebookItemNotRejectedException with the committed state unchanged.
    /// </summary>
    public async Task<VocabNotebookEntry> ResubmitAsync(
        Guid studentId, Guid itemId, string? word, string? partOfSpeech, string? meaning, CancellationToken cancellationToken = default)
    {
        var spelling = Required(word, VocabNotebook.MaxWordLength);
        var definition = Required(meaning, VocabNotebook.MaxMeaningLength);
        var pos = Optional(partOfSpeech, VocabNotebook.MaxPartOfSpeechLength);
        await RequireStudentAsync(studentId, cancellationToken).ConfigureAwait(false);
        var rejected = await _notebookRepository.FindAsync(studentId, itemId, cancellationToken).ConfigureAwait(false)
            ?? throw new VocabNotebookItemNotFoundException();
        if (rejected.Status != VocabNotebookItemStatus.Rejected) throw new VocabNotebookItemNotRejectedException();

        var submission = await _catalog.SubmitAsync(spelling, pos, definition, cancellationToken).ConfigureAwait(false);
        if (submission.Status == VocabSubmissionStatus.Rejected) throw new VocabCatalogUnavailableException();
        var adder = new VocabNotebookActor(rejected.AddedBy, rejected.AddedByTeacherId);
        var next = submission.Status == VocabSubmissionStatus.Approved
            ? ApprovedItem(studentId, adder, submission)
            : PendingItem(studentId, adder, submission);
        next.AddedAt = rejected.AddedAt;

        VocabNotebookItem stored;
        try
        {
            stored = await _notebookRepository.InRepeatableReadAsync(async () =>
            {
                var current = await _notebookRepository.FindAsync(studentId, itemId, cancellationToken).ConfigureAwait(false);
                if (current is null || current.Status != VocabNotebookItemStatus.Rejected || current.SubmissionId != rejected.SubmissionId)
                    throw new VocabNotebookItemNotRejectedException();
                VocabNotebookItem result;
                if (next.Key == current.Key)
                {
                    current.Status = next.Status;
                    current.BookId = next.BookId;
                    current.WordId = next.WordId;
                    current.MeaningId = next.MeaningId;
                    current.SubmissionId = next.SubmissionId;
                    current.ReviewComment = null;
                    current.Word = next.Word;
                    current.PartOfSpeech = next.PartOfSpeech;
                    current.Meaning = next.Meaning;
                    current.UpdatedAt = DateTimeOffset.UtcNow;
                    await _notebookRepository.UpdateAsync(current, cancellationToken).ConfigureAwait(false);
                    result = current;
                }
                else
                {
                    await _notebookRepository.RemoveAsync(current, cancellationToken).ConfigureAwait(false);
                    result = await MergeAsync(next, cancellationToken).ConfigureAwait(false);
                }

                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (VocabNotebookWriteConflictException)
        {
            throw new VocabNotebookItemNotRejectedException();
        }

        return await EntryAsync(stored, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid studentId, VocabNotebookActor actor, Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await _notebookRepository.FindAsync(studentId, itemId, cancellationToken).ConfigureAwait(false)
            ?? throw new VocabNotebookItemNotFoundException();
        if (actor.Type == VocabNotebookActorType.Student && item.AddedBy == VocabNotebookActorType.Teacher)
        {
            throw new VocabNotebookRemovalForbiddenException();
        }

        await _notebookRepository.RemoveAsync(item, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VocabularyPlanScopeEntry>> ReadNewWordSourcesAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        (await ReadLearningSourcesAsync(studentId, cancellationToken).ConfigureAwait(false)).NewWords;

    public async Task<IReadOnlySet<CardKey>> ReadPausedCardKeysAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        (await ReadLearningSourcesAsync(studentId, cancellationToken).ConfigureAwait(false)).PausedKeys;

    public async Task<VocabNotebookLearningSources> ReadLearningSourcesAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        await TrySyncPendingAsync(studentId, cancellationToken).ConfigureAwait(false);
        var items = await _notebookRepository.ListAsync(studentId, cancellationToken).ConfigureAwait(false);
        // Learning waits for approval: a pending item is learned once sync makes it ready; a rejected item never is.
        var newWords = items
            .Where(item => item.Status == VocabNotebookItemStatus.Ready)
            .OrderBy(item => item.AddedAt)
            .ThenBy(item => item.Id)
            .Select(Location)
            .ToList();
        var paused = items.Where(item => item.Status == VocabNotebookItemStatus.Pending).Select(item => item.Key).ToHashSet();
        return new VocabNotebookLearningSources(newWords, paused);
    }

    public async Task<(VocabularyPlanScopeEntry Location, VocabNotebookFamilySource Source)?> ReadVerifiedFamilyAsync(
        Guid studentId, CardKey key, CancellationToken cancellationToken)
    {
        var item = await _notebookRepository.FindByKeyAsync(studentId, key, cancellationToken).ConfigureAwait(false);
        if (item is not { Status: VocabNotebookItemStatus.Ready }) return null;
        var sources = item.FamilySources.Where(source => source.HasMeaningPair).ToArray();
        if (sources.Length == 0) return null;
        var outgoing = sources.Where(source => source.Direction == "outgoing").ToArray();
        if (outgoing.Length > 0) sources = outgoing;
        var source = sources[Random.Shared.Next(sources.Length)];
        var location = Location(item);
        if (source.Target is { } target)
            location = location with
            {
                BookId = target.BookId,
                Entry = location.Entry with { WordId = target.WordId, MeaningId = target.MeaningId }
            };
        return (location, source);
    }

    public async Task<VocabularyPlanScopeEntry?> ResolveSubmissionCardAsync(
        Guid studentId, CardKey key, string submissionId, CancellationToken cancellationToken = default)
    {
        var item = await _notebookRepository.FindByKeyAsync(studentId, key, cancellationToken).ConfigureAwait(false);
        if (item is not null) return item.Status == VocabNotebookItemStatus.Ready ? Location(item) : null;

        // No item for the key: it was removed, or an approval moved it to another key (the card stays as history).
        var submission = (await _catalog.LookupAsync([submissionId], cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(found => found.Id == submissionId);
        if (submission is not { Status: VocabSubmissionStatus.Approved }
            || submission.ResultBookId is null || submission.ResultWordId is null || submission.ResultMeaningId is null
            || (submission.ResultWordKey ?? submission.WordKey) != key.WordKey
            || (submission.ResultMeaningKey ?? submission.MeaningKey) != key.MeaningKey)
            return null;
        return new(submission.ResultBookId, false,
            new VocabularyScopeEntry(submission.ResultWordId, key.WordKey, submission.ResultMeaningId, key.MeaningKey, null));
    }

    private static VocabularyPlanScopeEntry Location(VocabNotebookItem item) => new(item.BookId!, false,
        new VocabularyScopeEntry(item.WordId!, item.WordKey, item.MeaningId!, item.MeaningKey, null))
    { Source = item.FamilyOnly ? VocabularySourceKind.NotebookFamily : VocabularySourceKind.Notebook, CoveredByPlan = false };

    private async Task TrySyncPendingAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var pending = await _notebookRepository.PendingSubmissionIdsAsync(studentId, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0) return;
        var results = new Dictionary<string, VocabMeaningSubmission>(StringComparer.Ordinal);
        try
        {
            foreach (var chunk in pending.Chunk(LookupChunkSize))
            {
                foreach (var submission in await _catalog.LookupAsync(chunk, cancellationToken).ConfigureAwait(false))
                    if (submission.Status != VocabSubmissionStatus.Pending) results[submission.Id] = submission;
            }
        }
        catch (VocabCatalogUnavailableException)
        {
            // Only the lookup can fail, before any row changed; the snapshot stays as it was.
            _logger?.LogWarning("Vocabulary notebook review results could not be synchronized for student {StudentId}.", studentId);
            return;
        }

        if (results.Count == 0) return;
        await _notebookRepository.InStudentWriteAsync(studentId, async () =>
        {
            var reviewed = await _notebookRepository.ReviewedPendingAsync(studentId, results.Keys.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in reviewed)
                await ApplyReviewAsync(studentId, item, results[item.SubmissionId!], cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return reviewed.Count;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyReviewAsync(Guid studentId, VocabNotebookItem item, VocabMeaningSubmission submission,
        CancellationToken cancellationToken)
    {
        if (submission.Status == VocabSubmissionStatus.Rejected)
        {
            // Keep the submission ID, snapshot and who added it; memory cards and attempts stay as history.
            item.Status = VocabNotebookItemStatus.Rejected;
            item.ReviewComment = ReviewComment(submission.ReviewComment);
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await _notebookRepository.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _notebookRepository.RemoveAsync(item, cancellationToken).ConfigureAwait(false);
        var approved = ApprovedItem(studentId, new VocabNotebookActor(item.AddedBy, item.AddedByTeacherId), submission);
        approved.Id = item.Id;
        approved.AddedAt = item.AddedAt;
        if (!submission.DefinitionKept)
        {
            // Lexarbor sets definitionKept = false for every revision, even one that only changed the part of speech
            // or phonetics, so the mark follows the flag rather than a comparison of the text.
            approved.Original = new VocabNotebookOriginal(item.Word, item.PartOfSpeech, item.Meaning);
            approved.ReviewComment = ReviewComment(submission.ReviewComment);
        }

        // Flush the removal first: an unchanged definition keeps the same key.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await MergeAsync(approved, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VocabNotebookEntry> UpsertAsync(VocabNotebookItem item, CancellationToken cancellationToken)
    {
        var stored = await MergeAsync(item, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await EntryAsync(stored, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VocabNotebookEntry> EntryAsync(VocabNotebookItem stored, CancellationToken cancellationToken)
    {
        var learned = await _cardRepository.FindExistingKeysAsync(stored.StudentId, [stored.Key], cancellationToken).ConfigureAwait(false);
        return new VocabNotebookEntry(stored, learned.Contains(stored.Key));
    }
    private async Task<VocabNotebookItem> MergeAsync(VocabNotebookItem item, CancellationToken cancellationToken,
        VocabNotebookItem? existing = null)
    {
        existing ??= await _notebookRepository.FindByKeyAsync(item.StudentId, item.Key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await _notebookRepository.AddAsync(item, cancellationToken).ConfigureAwait(false);
            return item;
        }

        var changed = false;
        if (Rank(item.Status) > Rank(existing.Status))
        {
            existing.Status = item.Status;
            existing.BookId = item.BookId;
            existing.WordId = item.WordId;
            existing.MeaningId = item.MeaningId;
            existing.SubmissionId = item.SubmissionId;
            existing.ReviewComment = null;
            existing.Word = item.Word;
            existing.PartOfSpeech = item.PartOfSpeech;
            existing.Meaning = item.Meaning;
            changed = true;
        }

        if (item.Original is not null)
        {
            existing.Original = item.Original;
            existing.ReviewComment = item.ReviewComment;
            changed = true;
        }

        foreach (var familySource in item.FamilySources)
        {
            if (!existing.CanAttachFamily(familySource)) continue;
            existing.AttachFamily(familySource);
            changed = true;
        }

        if (!item.FamilyOnly && item.Status == VocabNotebookItemStatus.Ready && existing.FamilyOnly)
        {
            existing.FamilyOnly = false;
            changed = true;
        }

        if (item.AddedBy == VocabNotebookActorType.Teacher && existing.AddedBy == VocabNotebookActorType.Student)
        {
            existing.AddedBy = VocabNotebookActorType.Teacher;
            existing.AddedByTeacherId = item.AddedByTeacherId;
            changed = true;
        }

        if (item.AddedAt < existing.AddedAt)
        {
            existing.AddedAt = item.AddedAt;
            changed = true;
        }

        if (changed)
        {
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await _notebookRepository.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        return existing;
    }

    private static int Rank(VocabNotebookItemStatus status) => status switch
    {
        VocabNotebookItemStatus.Ready => 3,
        VocabNotebookItemStatus.Pending => 2,
        _ => 1,
    };

    /// <summary>A reason or note: trimmed; empty becomes null; longer than Lexarbor's documented maximum is cut to it.</summary>
    private static string? ReviewComment(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length <= VocabNotebook.MaxReviewCommentLength) return trimmed;
        // Never split a surrogate pair: PostgreSQL text only accepts valid UTF-8.
        var length = char.IsHighSurrogate(trimmed[VocabNotebook.MaxReviewCommentLength - 1])
            ? VocabNotebook.MaxReviewCommentLength - 1
            : VocabNotebook.MaxReviewCommentLength;
        return trimmed[..length];
    }

    private static VocabNotebookItem ApprovedItem(Guid studentId, VocabNotebookActor actor, VocabMeaningSubmission submission)
    {
        var item = NewItem(studentId, actor,
            submission.ResultWordKey ?? submission.WordKey,
            submission.ResultMeaningKey ?? submission.MeaningKey,
            submission.ResultWord ?? submission.Word,
            submission.ResultPartOfSpeech ?? submission.PartOfSpeech,
            submission.ResultMeaning ?? submission.Meaning);
        if (submission.ResultBookId is null || submission.ResultWordId is null || submission.ResultMeaningId is null)
            throw new VocabCatalogUnavailableException();
        Locate(item, submission.ResultBookId, submission.ResultWordId, submission.ResultMeaningId);
        return item;
    }

    private static VocabNotebookItem PendingItem(Guid studentId, VocabNotebookActor actor, VocabMeaningSubmission submission)
    {
        var item = NewItem(studentId, actor, submission.WordKey, submission.MeaningKey,
            submission.Word, submission.PartOfSpeech, submission.Meaning);
        item.Status = VocabNotebookItemStatus.Pending;
        item.SubmissionId = submission.Id;
        return item;
    }

    private static VocabNotebookItem NewItem(
        Guid studentId, VocabNotebookActor actor, string wordKey, string meaningKey, string word, string? partOfSpeech, string meaning)
    {
        var now = DateTimeOffset.UtcNow;
        return new VocabNotebookItem
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            WordKey = wordKey,
            MeaningKey = meaningKey,
            Word = word,
            PartOfSpeech = string.IsNullOrWhiteSpace(partOfSpeech) ? null : partOfSpeech,
            Meaning = meaning,
            AddedBy = actor.Type,
            AddedByTeacherId = actor.Type == VocabNotebookActorType.Teacher ? actor.TeacherId : null,
            AddedAt = now,
            UpdatedAt = now,
        };
    }

    private static void Locate(VocabNotebookItem item, string bookId, string wordId, string meaningId)
    {
        item.Status = VocabNotebookItemStatus.Ready;
        item.BookId = bookId;
        item.WordId = wordId;
        item.MeaningId = meaningId;
        item.SubmissionId = null;
    }

    private async Task RequireStudentAsync(Guid studentId, CancellationToken cancellationToken)
    {
        if (!await _studentRepository.ExistsAsync(studentId, cancellationToken).ConfigureAwait(false))
            throw new VocabStudentNotFoundException();
    }

    private static string Required(string? value, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > maxLength) throw new VocabNotebookInvalidException();
        return trimmed;
    }

    private static string? Optional(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > maxLength) throw new VocabNotebookInvalidException();
        return trimmed;
    }
}
