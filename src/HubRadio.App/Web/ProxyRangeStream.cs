namespace HubRadio.App.Web;

/// <summary>FR-144: a read-only window (<c>start</c>, <c>length</c>) over an open proxy file. Owns the handle.</summary>
internal sealed class ProxyRangeStream : Stream
{
    private readonly Projects.ProxyReadHandle handle;
    private readonly long start;
    private readonly long length;
    private long position;

    internal ProxyRangeStream(Projects.ProxyReadHandle handle, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (start < 0 || length <= 0 || start > handle.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        this.handle = handle;
        this.start = start;
        this.length = length;
        handle.Stream.Position = start;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int allowed = (int)Math.Min(buffer.Length, length - position);
        if (allowed <= 0)
        {
            return 0;
        }

        int read = handle.Stream.Read(buffer[..allowed]);
        position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int allowed = (int)Math.Min(buffer.Length, length - position);
        if (allowed <= 0)
        {
            return 0;
        }

        int read = await handle.Stream.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => length + offset,
        };
        if (target < 0 || target > length)
        {
            throw new IOException("The seek is outside the proxy range.");
        }

        handle.Stream.Position = start + target;
        position = target;
        return position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            handle.Dispose();
        }

        base.Dispose(disposing);
    }
}
