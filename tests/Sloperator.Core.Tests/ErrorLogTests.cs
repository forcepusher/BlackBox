using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Data/errors.log: every error Sloperator showed, with the exception behind it.</summary>
public sealed class ErrorLogTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "bb-errorlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ErrorLog.Stop();
        try { Directory.Delete(_home, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void An_error_is_written_with_where_it_was_shown_what_it_said_and_the_whole_exception()
    {
        var paths = new AppPaths(_home);
        ErrorLog.Start(paths);
        Exception thrown;
        try { throw new ConnectionLostException("The server broke off its answer: Upstream error from Nvidia: Internal server error", false); }
        catch (Exception ex) { thrown = ex; }

        ErrorLog.Write("chat \"Build it\"", "Connection lost (The server broke off its answer: Upstream error from Nvidia: Internal server error). Reconnecting, attempt 1 of 12…", thrown);
        ErrorLog.Write("the server at https://ai-gateway.kilo.ai/api/v1", "Offline: Could not reach it.");

        var log = File.ReadAllText(Path.Combine(paths.DataDir, "errors.log"));
        Assert.Contains("(chat \"Build it\")" + Environment.NewLine + "Connection lost (The server broke off its answer", log);
        Assert.Contains(typeof(ConnectionLostException).FullName + ": The server broke off its answer", log);
        Assert.Contains(nameof(An_error_is_written_with_where_it_was_shown_what_it_said_and_the_whole_exception), log); // its stack trace
        Assert.Contains("(the server at https://ai-gateway.kilo.ai/api/v1)" + Environment.NewLine + "Offline: Could not reach it." + Environment.NewLine + Environment.NewLine, log);
    }

    [Fact]
    public void Nothing_is_written_before_the_log_is_started()
    {
        // The tests and the command-line runner show their errors themselves.
        var paths = new AppPaths(_home);
        ErrorLog.Start(paths);
        ErrorLog.Stop();
        ErrorLog.Write("nowhere", "Not written.");
        Assert.False(File.Exists(ErrorLog.FileName(paths)));
    }

    [Fact]
    public void A_log_grown_too_long_keeps_its_newest_entries_from_the_start_of_one()
    {
        var nl = Environment.NewLine;
        var entry = (int i) => $"2026-10-23 20:45:{i % 60:00} (chat){nl}Error {i}{nl}{nl}";
        var log = string.Concat(Enumerable.Range(0, 40_000).Select(entry));

        var kept = ErrorLog.Newest(log);

        Assert.True(kept.Length is > 0 and <= 512 * 1024);
        Assert.StartsWith("2026-10-23 20:45:", kept);
        Assert.EndsWith(entry(39_999), kept);
        var small = string.Concat(Enumerable.Range(0, 10).Select(entry));
        Assert.Equal(small, ErrorLog.Newest(small));
    }
}
