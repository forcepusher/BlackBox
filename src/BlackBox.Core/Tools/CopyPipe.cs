namespace BlackBox.Core.Tools;

/// <summary>
/// Bytes from one writer to one reader, in order: what <see cref="CopyTool"/> streams through on
/// the way from one end of a copy to the other, so a copy of any size needs only a little memory.
/// Up to <see cref="MemoryLimit"/> bytes wait in memory. Beyond that the writer either waits for
/// the reader (files on this PC, which can wait) or, with spilling on, the bytes wait on disk (an
/// archive coming from a server: SSH.NET takes in what the server sends whether or not anyone
/// reads it, so the reader of that stream must never stop). On disk they wait in temporary files
/// of <see cref="SegmentSize"/> bytes, each deleted as soon as it is read, so the disk holds about
/// what waits rather than all that ever waited; once the reader catches up, new bytes wait in
/// memory again.
/// </summary>
internal sealed class CopyPipe : IDisposable
{
    internal const long DefaultSegmentSize = 64L << 20;

    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly Queue<Segment> _segments = new();
    private readonly bool _spill;
    private Segment? _last;
    private int _offset;
    private long _inMemory, _onDisk;
    private bool _done;
    private Exception? _failure;

    public CopyPipe(long memoryLimit, bool spillToDisk, long segmentSize = DefaultSegmentSize)
    {
        MemoryLimit = memoryLimit;
        SegmentSize = segmentSize;
        _spill = spillToDisk;
        Writer = new WriteEnd(this);
        Reader = new ReadEnd(this);
    }

    public long MemoryLimit { get; }

    /// <summary>The size of each temporary file the bytes past the memory limit wait in.</summary>
    public long SegmentSize { get; }

    /// <summary>The end the source writes to; <see cref="Complete"/> marks the end of the data.</summary>
    public Stream Writer { get; }

    /// <summary>The end the destination reads from; a read returns 0 once all is read and the writer completed.</summary>
    public Stream Reader { get; }

    /// <summary>Bytes written and read so far (progress).</summary>
    public long BytesWritten { get; private set; }
    public long BytesRead { get; private set; }

    /// <summary>The most bytes that waited on disk at once (0 if the disk was never needed).</summary>
    public long SpilledPeak { get; private set; }

    public void Complete()
    {
        lock (_gate)
        {
            _done = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Stops both ends: what waits in a read or a write throws <see cref="CopyStoppedException"/>.</summary>
    public void Fail(Exception reason)
    {
        lock (_gate)
        {
            _failure ??= reason;
            Monitor.PulseAll(_gate);
        }
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;
        lock (_gate)
        {
            while (true)
            {
                if (_failure is not null) throw new CopyStoppedException(_failure);
                if (_done) throw new InvalidOperationException("The copy's data was already complete.");
                // With spilling on the writer never waits; without, it waits for room in memory.
                if (_spill || _inMemory == 0 || _inMemory + data.Length <= MemoryLimit) break;
                Monitor.Wait(_gate);
            }
            BytesWritten += data.Length;
            // While anything waits on disk, new bytes go after it.
            if (_segments.Count == 0 && (_inMemory == 0 || _inMemory + data.Length <= MemoryLimit))
            {
                _chunks.Enqueue(data.ToArray());
                _inMemory += data.Length;
            }
            else
                ToDisk(data);
            Monitor.PulseAll(_gate);
        }
    }

    private void ToDisk(ReadOnlySpan<byte> data)
    {
        _onDisk += data.Length;
        SpilledPeak = Math.Max(SpilledPeak, _onDisk);
        while (!data.IsEmpty)
        {
            if (_last is null || _last.Written == SegmentSize)
                _segments.Enqueue(_last = new Segment());
            var n = (int)Math.Min(data.Length, SegmentSize - _last.Written);
            _last.File.Position = _last.Written;
            _last.File.Write(data[..n]);
            _last.Written += n;
            data = data[n..];
        }
    }

    private int ReadInto(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        lock (_gate)
        {
            while (true)
            {
                if (_failure is not null) throw new CopyStoppedException(_failure);
                // What waits in memory was written before what waits on disk.
                if (_chunks.Count > 0)
                {
                    var chunk = _chunks.Peek();
                    var n = Math.Min(buffer.Length, chunk.Length - _offset);
                    chunk.AsSpan(_offset, n).CopyTo(buffer);
                    _offset += n;
                    _inMemory -= n;
                    if (_offset == chunk.Length)
                    {
                        _chunks.Dequeue();
                        _offset = 0;
                    }
                    return Took(n);
                }
                if (_segments.TryPeek(out var first))
                {
                    first.File.Position = first.Read;
                    var n = first.File.Read(buffer[..(int)Math.Min(buffer.Length, first.Written - first.Read)]);
                    if (n == 0)
                        throw new IOException("The copy's temporary file ended early.");
                    first.Read += n;
                    _onDisk -= n;
                    // A file read to its end goes: a full one gets nothing more, and the last one read
                    // to its end means the reader caught up (new bytes wait in memory again).
                    if (first.Read == first.Written && (first.Written == SegmentSize || _segments.Count == 1))
                    {
                        _segments.Dequeue().File.Dispose();
                        if (_segments.Count == 0)
                            _last = null;
                    }
                    return Took(n);
                }
                if (_done)
                    return 0;
                Monitor.Wait(_gate);
            }
        }

        int Took(int n)
        {
            BytesRead += n;
            Monitor.PulseAll(_gate);
            return n;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _failure ??= new ObjectDisposedException(nameof(CopyPipe));
            Monitor.PulseAll(_gate);
            while (_segments.TryDequeue(out var segment))
                segment.File.Dispose();
            _last = null;
        }
    }

    /// <summary>
    /// One temporary file of the bytes waiting on disk; the system deletes it when it is closed. Not
    /// buffered: the bytes come and go in chunks of tens of kilobytes already.
    /// </summary>
    private sealed class Segment
    {
        public FileStream File { get; } = new(Path.Combine(Path.GetTempPath(), $"blackbox-copy-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 0, FileOptions.DeleteOnClose);
        public long Written, Read;
    }

    private sealed class WriteEnd(CopyPipe pipe) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => pipe.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => pipe.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            pipe.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            pipe.Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }
    }

    private sealed class ReadEnd(CopyPipe pipe) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => pipe.ReadInto(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => pipe.ReadInto(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(pipe.ReadInto(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(pipe.ReadInto(buffer.AsSpan(offset, count)));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>The other end of a <see cref="CopyPipe"/> failed or the copy was stopped; the reason is the inner exception.</summary>
internal sealed class CopyStoppedException(Exception reason) : IOException("The copy was stopped: " + reason.Message, reason);
