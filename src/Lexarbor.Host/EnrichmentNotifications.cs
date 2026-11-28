using System.Threading.Channels;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Host;

public sealed class EnrichmentNotifications : IEnrichmentChangeNotifier
{
    private readonly Channel<string> _words = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly Channel<EnrichmentTargetKey> _audioTargets = Channel.CreateBounded<EnrichmentTargetKey>(new BoundedChannelOptions(256)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    public void Notify(IReadOnlyList<string> wordIds, IReadOnlyList<EnrichmentTargetKey> audioTargets)
    {
        foreach (var target in audioTargets.Take(256)) _audioTargets.Writer.TryWrite(target);
        // Enqueue the target before waking the worker through the word channel.
        Notify(wordIds);
    }
    public void Notify(IReadOnlyList<string> wordIds)
    {
        if (wordIds.Count == 0) _words.Writer.TryWrite("");
        foreach (var id in wordIds.Take(256)) _words.Writer.TryWrite(id);
    }
    public IReadOnlyList<string> Drain()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 256 && _words.Reader.TryRead(out var word); i++)
            if (word.Length > 0) result.Add(word);
        return result.ToArray();
    }
    public Task WaitAsync(CancellationToken token) => _words.Reader.WaitToReadAsync(token).AsTask();
    public IReadOnlyList<EnrichmentTargetKey> DrainAudioTargets()
    {
        var result = new HashSet<EnrichmentTargetKey>();
        for (var i = 0; i < 256 && _audioTargets.Reader.TryRead(out var target); i++) result.Add(target);
        return result.ToArray();
    }
}
