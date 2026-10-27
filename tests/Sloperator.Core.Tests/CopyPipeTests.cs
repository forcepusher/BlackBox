using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// The pipe a copy streams through: what may wait in memory, the temporary files that take the
/// rest when the writer must not wait, and a failure at one end stopping the other.
/// </summary>
public class CopyPipeTests
{
    private const long MB = 1 << 20;

    // The byte at position i of a test stream: no short period, so a lost, doubled or swapped chunk shows.
    private static byte At(long i) => (byte)((ulong)(i + 1) * 0x9E3779B97F4A7C15UL >> 56);

    private static void WriteRange(CopyPipe pipe, long from, int count)
    {
        var data = new byte[count];
        for (var i = 0; i < count; i++)
            data[i] = At(from + i);
        pipe.Writer.Write(data, 0, count);
    }

    /// <summary>Writes <paramref name="count"/> bytes of the test stream from position <paramref name="from"/>, <paramref name="chunk"/> at a time.</summary>
    private static void Write(CopyPipe pipe, long count, int chunk, long from = 0)
    {
        for (var at = from; at < from + count; at += chunk)
            WriteRange(pipe, at, (int)Math.Min(chunk, from + count - at));
    }

    /// <summary>
    /// Reads from position <paramref name="from"/> to <paramref name="until"/>, or to the end, checking every byte;
    /// <paramref name="pause"/> is called after each read. Returns the position reached.
    /// </summary>
    private static long ReadAll(CopyPipe pipe, Func<int> size, Action? pause = null, long from = 0, long until = long.MaxValue)
    {
        var at = from;
        var buffer = new byte[256 << 10];
        int n;
        while (at < until && (n = pipe.Reader.Read(buffer, 0, (int)Math.Min(Math.Min(buffer.Length, size()), until - at))) > 0)
        {
            for (var i = 0; i < n; i++)
                if (buffer[i] != At(at + i))
                    Assert.Fail($"Byte {at + i} is {buffer[i]}, not {At(at + i)}.");
            at += n;
            pause?.Invoke();
        }
        return at;
    }

    private static string[] SpillFiles() => Directory.GetFiles(Path.GetTempPath(), "sloperator-copy-*.tmp");

    /// <summary>
    /// The bytes in the spill files made since <paramref name="before"/>, once it is <paramref name="expected"/>
    /// (a closed file can linger a moment while a virus scanner still has it open).
    /// </summary>
    private static long OnDisk(string[] before, long expected)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            var bytes = SpillFiles().Except(before).Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0; } });
            if (bytes == expected || DateTime.UtcNow > until)
                return bytes;
            Thread.Sleep(50);
        }
    }

    [Fact]
    public async Task Without_spilling_the_writer_waits_while_the_memory_is_full()
    {
        using var pipe = new CopyPipe(MB, spillToDisk: false);
        const long total = 24 * MB;
        var most = 0L;
        var reads = 0;
        var write = Task.Run(() =>
        {
            Write(pipe, total, 60_000);
            pipe.Complete();
        }, TestContext.Current.CancellationToken);
        // A reader that starts late and reads slowly: what waits never passes the limit, and the
        // writer holds back rather than spill.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(write.IsCompleted);
        var read = Task.Run(() => ReadAll(pipe, () => 16 << 10, () =>
        {
            most = Math.Max(most, pipe.BytesWritten - pipe.BytesRead);
            if (++reads % 32 == 0)
                Thread.Sleep(1);
        }), TestContext.Current.CancellationToken);
        Assert.Equal(total, await read);
        await write;
        Assert.InRange(most, pipe.MemoryLimit - 120_000, pipe.MemoryLimit);
        Assert.Equal(0, pipe.SpilledPeak);
    }

    [Fact]
    public void With_spilling_the_writer_never_waits_and_the_files_go_at_the_end()
    {
        var before = SpillFiles();
        const long total = 24 * MB;
        using (var pipe = new CopyPipe(MB, spillToDisk: true))
        {
            // No reader yet: the first megabyte waits in memory, all the rest on disk.
            Write(pipe, total, 60_000);
            Assert.InRange(pipe.SpilledPeak, total - pipe.MemoryLimit, total - pipe.MemoryLimit + 60_000);
            Assert.Equal(pipe.SpilledPeak, OnDisk(before, pipe.SpilledPeak));
            pipe.Complete();
            Assert.Equal(total, ReadAll(pipe, () => 100_000));
        }
        Assert.Equal(0, OnDisk(before, 0));
    }

    [Fact]
    public void The_disk_holds_what_waits_not_all_that_ever_waited()
    {
        var before = SpillFiles();
        using var pipe = new CopyPipe(MB, spillToDisk: true, segmentSize: MB);
        // No reader yet: 1 MB waits in memory, 19 MB on disk in files of 1 MB.
        Write(pipe, 20 * MB, 64 << 10);
        Assert.Equal(19 * MB, pipe.SpilledPeak);
        Assert.Equal(19 * MB, OnDisk(before, 19 * MB));
        // The reader takes 15 MB, the 1 in memory and then 14 from disk: those files are gone.
        var at = ReadAll(pipe, () => 100_000, until: 15 * MB);
        Assert.Equal(5 * MB, OnDisk(before, 5 * MB));
        // While anything waits on disk, new bytes go after it.
        Write(pipe, 3 * MB, 64 << 10, from: 20 * MB);
        Assert.Equal(8 * MB, OnDisk(before, 8 * MB));
        // Caught up: nothing is left on disk, and new bytes wait in memory again.
        at = ReadAll(pipe, () => 100_000, from: at, until: 23 * MB);
        Assert.Equal(0, OnDisk(before, 0));
        Write(pipe, MB / 2, 64 << 10, from: 23 * MB);
        Assert.Equal(0, OnDisk(before, 0));
        pipe.Complete();
        Assert.Equal(23 * MB + MB / 2, ReadAll(pipe, () => 100_000, from: at));
        Assert.Equal(19 * MB, pipe.SpilledPeak);
    }

    [Fact]
    public async Task Bytes_keep_their_order_onto_disk_and_back_to_memory()
    {
        // Files of an odd size, so writes and reads cross from one file to the next.
        using var pipe = new CopyPipe(256 << 10, spillToDisk: true, segmentSize: 300_000);
        const long total = 48 * MB;
        // A writer in bursts and a reader that stalls now and then: the pipe spills, catches up, and spills again.
        var write = Task.Run(() =>
        {
            var random = new Random(3);
            for (long at = 0; at < total;)
            {
                var n = (int)Math.Min(total - at, random.Next(1, 200_000));
                WriteRange(pipe, at, n);
                at += n;
                if (random.Next(16) == 0)
                    Thread.Sleep(1);
            }
            pipe.Complete();
        }, TestContext.Current.CancellationToken);
        var sizes = new Random(4);
        var read = Task.Run(() => ReadAll(pipe, () => sizes.Next(1, 120_000), () =>
        {
            if (sizes.Next(24) == 0)
                Thread.Sleep(3);
        }), TestContext.Current.CancellationToken);
        Assert.Equal(total, await read);
        await write;
        Assert.InRange(pipe.SpilledPeak, 1, total);
    }

    [Fact]
    public async Task A_failure_at_one_end_stops_the_other()
    {
        using var pipe = new CopyPipe(MB, spillToDisk: false);
        // Nobody reads: the writer waits once the memory is full, until the reader's end fails.
        var write = Task.Run(() => Write(pipe, 8 * MB, 60_000), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(write.IsCompleted);
        pipe.Fail(new IOException("No space left on device"));
        var stopped = await Assert.ThrowsAsync<CopyStoppedException>(() => write);
        Assert.Equal("No space left on device", stopped.InnerException!.Message);
        Assert.Throws<CopyStoppedException>(() => pipe.Reader.Read(new byte[16], 0, 16));
    }

    [Fact]
    public async Task A_copy_on_this_PC_streams_through_a_pipe_smaller_than_its_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bb-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "site", "data"));
        var dump = new byte[40 << 20];
        new Random(9).NextBytes(dump);
        await File.WriteAllBytesAsync(Path.Combine(dir, "site", "data", "dump.bin"), dump, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "site", "index.html"), "v2\n", TestContext.Current.CancellationToken);
        var saved = CopyTool.MemoryLimit;
        CopyTool.MemoryLimit = 1 << 20;
        try
        {
            var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = dir };
            var copied = await new CopyTool().ExecuteAsync(new() { ["from"] = "site", ["to"] = "copy" }, ctx, CancellationToken.None);
            Assert.False(copied.IsError, copied.Text);
            Assert.StartsWith("Copied 2 files (40 MB) to ", copied.Text);
            Assert.Equal(dump, await File.ReadAllBytesAsync(Path.Combine(dir, "copy", "data", "dump.bin"), TestContext.Current.CancellationToken));
            Assert.Equal("v2\n", await File.ReadAllTextAsync(Path.Combine(dir, "copy", "index.html"), TestContext.Current.CancellationToken));
        }
        finally
        {
            CopyTool.MemoryLimit = saved;
            Directory.Delete(dir, recursive: true);
        }
    }
}
