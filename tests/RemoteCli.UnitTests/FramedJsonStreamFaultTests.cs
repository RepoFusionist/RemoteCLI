using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Common.Protocol;

namespace RemoteCli.UnitTests;

public sealed class FramedJsonStreamFaultTests
{
    private static readonly TimeSpan CompletionLimit = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task PartialHeaderEofFailsWithinBoundedTime()
    {
        using var stream = new MemoryStream([0, 0]);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await FramedJsonStream.ReadAsync<ProtocolEnvelope>(stream, CancellationToken.None)
                .WaitAsync(CompletionLimit));

        AssertCompletedWithinLimit(stopwatch, "partial header EOF");
    }

    [Fact]
    public async Task PartialPayloadEofFailsWithinBoundedTime()
    {
        var frame = new byte[sizeof(int) + 3];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), 8);
        frame[sizeof(int)] = (byte)'{';
        frame[sizeof(int) + 1] = (byte)'}';
        frame[sizeof(int) + 2] = (byte)' ';
        using var stream = new MemoryStream(frame);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await FramedJsonStream.ReadAsync<ProtocolEnvelope>(stream, CancellationToken.None)
                .WaitAsync(CompletionLimit));

        AssertCompletedWithinLimit(stopwatch, "partial payload EOF");
    }

    [Fact]
    public async Task CancellationDuringSilentReadCompletesWithinBoundedTime()
    {
        using var stream = new SilentReadStream();
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var readTask = FramedJsonStream.ReadAsync<ProtocolEnvelope>(stream, cancellation.Token);

        await stream.ReadStarted.WaitAsync(CompletionLimit);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await readTask.WaitAsync(CompletionLimit));

        AssertCompletedWithinLimit(stopwatch, "cancelled silent read");
    }

    [Fact]
    public async Task UnknownProtocolFrameIsRejectedWithinBoundedTime()
    {
        var envelope = new ProtocolEnvelope(
            ProtocolConstants.CurrentVersion,
            "unknown.test.frame",
            Guid.NewGuid(),
            1,
            JsonSerializer.SerializeToElement(new { }));
        var frame = CreateFrame(ProtocolJson.Serialize(envelope));
        using var stream = new MemoryStream(frame);
        var stopwatch = Stopwatch.StartNew();

        var decoded = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(stream, CancellationToken.None)
            .WaitAsync(CompletionLimit);
        Assert.NotNull(decoded);
        Assert.Throws<InvalidDataException>(() => ProtocolJson.DeserializeEnvelope(ProtocolJson.Serialize(decoded)));

        AssertCompletedWithinLimit(stopwatch, "unknown protocol frame");
    }

    [Fact]
    public async Task OversizedFrameIsRejectedFromHeaderWithinBoundedTime()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, ProtocolConstants.MaximumFrameBytes + 1);
        using var stream = new MemoryStream(header);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await FramedJsonStream.ReadAsync<ProtocolEnvelope>(stream, CancellationToken.None)
                .WaitAsync(CompletionLimit));

        AssertCompletedWithinLimit(stopwatch, "oversized frame header");
    }

    private static byte[] CreateFrame(byte[] payload)
    {
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), payload.Length);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static void AssertCompletedWithinLimit(Stopwatch stopwatch, string scenario)
    {
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < CompletionLimit,
            $"{scenario} took {stopwatch.Elapsed.TotalMilliseconds:F0} ms; limit is {CompletionLimit.TotalMilliseconds:F0} ms.");
    }

    private sealed class SilentReadStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
