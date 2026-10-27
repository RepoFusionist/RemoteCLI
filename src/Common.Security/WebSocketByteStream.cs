using System.Net.WebSockets;

namespace Common.Security;

public sealed class WebSocketByteStream : Stream
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly WebSocket _socket;
    private readonly bool _ownsSocket;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[] _readBuffer = [];
    private int _readOffset;
    private bool _remoteClosed;
    private bool _disposed;

    public WebSocketByteStream(WebSocket socket, bool ownsSocket = true)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _ownsSocket = ownsSocket;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return 0;
        }

        if (_readOffset >= _readBuffer.Length)
        {
            if (_remoteClosed)
            {
                return 0;
            }

            _readBuffer = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
            _readOffset = 0;
            if (_readBuffer.Length == 0)
            {
                return 0;
            }
        }

        var count = Math.Min(buffer.Length, _readBuffer.Length - _readOffset);
        _readBuffer.AsMemory(_readOffset, count).CopyTo(buffer);
        _readOffset += count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("Encrypted transport frame exceeds the configured size limit.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _writeLock.Dispose();
            if (_ownsSocket)
            {
                _socket.Abort();
                _socket.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private async Task<byte[]> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var scratch = new byte[16 * 1024];
        while (true)
        {
            var result = await _socket.ReceiveAsync(scratch.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _remoteClosed = true;
                return [];
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new InvalidDataException("Only binary relay frames are accepted.");
            }

            if (message.Length + result.Count > MaximumMessageBytes)
            {
                throw new InvalidDataException("Encrypted transport frame exceeds the configured size limit.");
            }

            message.Write(scratch, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }
}
