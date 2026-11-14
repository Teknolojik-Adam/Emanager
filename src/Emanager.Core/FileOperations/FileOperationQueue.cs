using System.Threading.Channels;

namespace Emanager.Core.FileOperations;

/// <summary>Bounded FIFO queue used by hosts that want admission control before the manager.</summary>
public sealed class FileOperationQueue
{
    private readonly Channel<FileOperationJob> _channel;
    public FileOperationQueue(int capacity = 128) => _channel = Channel.CreateBounded<FileOperationJob>(new BoundedChannelOptions(Math.Max(1, capacity)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    public ValueTask EnqueueAsync(FileOperationJob job, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(job, cancellationToken);
    public void Complete() => _channel.Writer.TryComplete();
    public IAsyncEnumerable<FileOperationJob> ReadAllAsync(CancellationToken cancellationToken = default) => _channel.Reader.ReadAllAsync(cancellationToken);
}
