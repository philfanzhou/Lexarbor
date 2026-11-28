using System.Threading.Channels;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Host;

public sealed class MeaningSubmissionNotifications : IMeaningSubmissionChangeNotifier
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    public void Notify() => _channel.Writer.TryWrite(true);
    public void Drain() { while (_channel.Reader.TryRead(out _)) { } }
    public Task WaitAsync(CancellationToken token) => _channel.Reader.WaitToReadAsync(token).AsTask();
}
