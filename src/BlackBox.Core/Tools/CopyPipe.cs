namespace BlackBox.Core.Tools;

/// <summary>
/// Bytes from one writer to one reader, in order: what <see cref="CopyTool"/> streams through on
/// the way from one end of a copy to the other, so a copy of any size needs only a little memory.
/// Up to <see cref="MemoryLimit"/> bytes wait in memory. Beyond that the writer either waits for
/// the reader (files on this PC, which can wait) or, with spilling on, the bytes wait in a
/// temporary file (an archive coming from a server: SSH.NET takes in what the server sends
/// whether or not anyone reads it, so the reader of that stream must never stop). The file is
/// emptied whenever the reader catches up and deleted at the end.
/// </summary>
internal sealed class CopyPipe : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly bool _spill;
    private int _offset;
    private long _inMemory;
    private FileStream? _file;
    private long _fileWritten, _fileRead;
    private bool _spilling;
    private bool _done;
    private Exception? _failure;

    public CopyPipe(long memoryLimit, bool spillToDisk)
    {
        MemoryLimit = memoryLimit;
        _spill = spillToDisk;
        Writer = new WriteEnd(this);
        Reader = new ReadEnd(this);
    }

    public long MemoryLimit { get; }

    /// <summary>The end the source writes to; <see cref="Complete"/> marks the end of the data.</summary>
    public Stream Writer { get; }

    /// <summary>The end the destination reads from; a read returns 0 once all is read and the writer completed.</summary>
    public Stream Reader { get; }

    /// <summary>Bytes written and read so far (progress).</summary>
    public long BytesWritten { get; private set; }
    public long BytesRead { get; private set; }

    /// <summary>The most bytes that waited in the temporary file at once (0 if it was never needed).</summary>
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
                if (_spilling) break;
                if (_inMemory == 0 || _inMemory + data.Length <= MemoryLimit) break;
                if (_spill)
                {
                    _spilling = true;
                    break;
                }
                Monitor.Wait(_gate);
            }
            if (_spilling)
            {
                _file ??= new FileStream(Path.Combine(Path.GetTempPath(), $"blackbox-copy-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
                _file.Position = _fileWritten;
                _file.Write(data);
                _fileWritten += data.Length;
                SpilledPeak = Math.Max(SpilledPeak, _fileWritten - _fileRead);
            }
            else
            {
                _chunks.Enqueue(data.ToArray());
                _inMemory += data.Length;
            }
            BytesWritten += data.Length;
            Monitor.PulseAll(_gate);
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
                // What waits in memory was written before what waits in the file.
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
                if (_fileRead < _fileWritten)
                {
                    _file!.Position = _fileRead;
                    var n = _file.Read(buffer[..(int)Math.Min(buffer.Length, _fileWritten - _fileRead)]);
                    _fileRead += n;
                    if (_fileRead == _fileWritten)
                    {
                        // Caught up: the file starts over, and new bytes go to memory again.
                        _file.SetLength(0);
                        _fileRead = _fileWritten = 0;
                        _spilling = false;
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
            _file?.Dispose();
            _file = null;
        }
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
