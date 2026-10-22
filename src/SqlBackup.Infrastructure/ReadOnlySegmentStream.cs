namespace SqlBackup.Infrastructure;

// A multipart request may seek or dispose its input; neither may affect another part's boundaries.
public sealed class ReadOnlySegmentStream(Stream source, long offset, long length) : Stream
{
    private long position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int bufferOffset, int count)
    {
        source.Position = offset + position;
        int read = source.Read(buffer, bufferOffset, (int)Math.Min(count, length - position));
        position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        source.Position = offset + position;
        int count = (int)Math.Min(buffer.Length, length - position);
        int read = await source.ReadAsync(buffer[..count], cancellationToken);
        position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int bufferOffset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(bufferOffset, count), cancellationToken).AsTask();
    }

    public override long Seek(long value, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => value,
            SeekOrigin.Current => position + value,
            SeekOrigin.End => length + value,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > length)
        {
            throw new IOException("Multipart seek is outside the selected segment.");
        }

        position = target;
        return position;
    }

    public override void Flush()
    {
        // Read-only view; the source remains owned by the transfer service.
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int bufferOffset, int count)
    {
        throw new NotSupportedException();
    }
}
