using System.Buffers.Binary;
using System.Text.Json;

namespace Common.Protocol;

public static class FramedJsonStream
{
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        => await ReadAsync<T>(stream, cancellationToken, ProtocolConstants.MaximumFrameBytes).ConfigureAwait(false);

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken, int maximumFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateMaximumFrameBytes(maximumFrameBytes);
        var header = new byte[sizeof(int)];
        var firstByte = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstByte == 0)
        {
            return default;
        }

        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 || length > maximumFrameBytes)
        {
            throw new InvalidDataException("Protocol frame has an invalid size.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, ProtocolJson.Options)
            ?? throw new InvalidDataException("Protocol frame was empty.");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, SemaphoreSlim writeLock, CancellationToken cancellationToken)
        => await WriteAsync(stream, value, writeLock, cancellationToken, ProtocolConstants.MaximumFrameBytes).ConfigureAwait(false);

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken,
        int maximumFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(writeLock);
        ValidateMaximumFrameBytes(maximumFrameBytes);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJson.Options);
        if (payload.Length is <= 0 || payload.Length > maximumFrameBytes)
        {
            throw new InvalidDataException("Protocol frame exceeds the configured size limit.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void ValidateMaximumFrameBytes(int maximumFrameBytes)
    {
        if (maximumFrameBytes is <= 0 || maximumFrameBytes > ProtocolConstants.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        }
    }
}
