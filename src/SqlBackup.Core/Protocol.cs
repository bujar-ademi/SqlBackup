using System.Text.Json;

namespace SqlBackup.Core;

public static class Protocol
{
    public const string PipeName = "SqlBackup.Management.v1";
    public const int MaximumFrameSize = 1048576;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaximumFrameSize)
        {
            throw new InvalidDataException("Request exceeds the message limit.");
        }

        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaximumFrameSize)
        {
            throw new InvalidDataException("Invalid message length.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload)
            ?? throw new InvalidDataException("Invalid message content.");
    }
}

