using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

// The same error twice in a run points the model to web_search (it didn't search on its own).
public class ErrorRepeatsTests
{
    // As run_shell returned it in the Excel run, four times with only the script line changing.
    private static ToolResult ChartError(int line) => ToolResult.Error($"""
        Exit code 1
        Method invocation failed because [System.__ComObject] does not contain a method named 'Add'.
        At C:\Repos\BlackBox\.dev\blindspots\work\make_budget.ps1:{line} char:1
        + $chart = $ws.ChartObjects.Add(300, 10, 450, 250)
        + ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
            + CategoryInfo          : InvalidOperation: (:) [], RuntimeException
            + FullyQualifiedErrorId : MethodNotFound
        """);

    [Fact]
    public void The_second_time_an_error_comes_up_the_model_is_pointed_to_web_search()
    {
        var errors = new ErrorRepeats();

        Assert.Null(errors.Check(ChartError(41)));
        Assert.Contains("web_search", errors.Check(ChartError(44)));
        Assert.Null(errors.Check(ChartError(47))); // once is enough

        // Another error counts on its own; results that went fine never count.
        Assert.Null(errors.Check(ToolResult.Error("File not found: C:\\x.txt")));
        Assert.Null(errors.Check(new ToolResult("Exit code 0\nall good")));
        Assert.NotNull(errors.Check(ToolResult.Error("File not found: C:\\x.txt")));
    }

    [Fact]
    public void A_powershell_error_record_counts_even_when_the_script_went_on_with_exit_code_0()
    {
        var quiet = new ToolResult(ChartError(41).Text.Replace("Exit code 1", "Exit code 0"));
        Assert.Equal("method invocation failed because [system.__comobject] does not contain a method named 'add'.", ErrorRepeats.Signature(quiet));
    }

    [Fact]
    public async Task In_a_run_the_note_comes_only_when_the_model_has_web_search()
    {
        foreach (var web in new[] { true, false })
        {
            var server = new ScriptedServer(
                Sse.ToolCall("r1", "read_file", """{"path":"missing.txt"}"""),
                Sse.ToolCall("r2", "read_file", """{"path":"missing.txt"}"""),
                Sse.Text("It isn't there."));
            var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
            var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), new ChatRequestOptions(),
                ToolRegistry.CreateDefault(includeScreenTools: false, includeWebTools: web), ctx, "sys");

            await session.RunAsync("read it", new RecordingObserver(), CancellationToken.None);

            var results = session.History.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content!).ToList();
            Assert.DoesNotContain("web_search", results[0]);
            Assert.Equal(web, results[1].Contains("look it up with web_search"));
        }
    }
}
