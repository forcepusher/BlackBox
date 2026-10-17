using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace BlackBox.Core.Tests;

// Failed tries with no look-up in between point the model to web_search (it didn't search on its own).
public class GuessworkTests
{
    private static readonly ShellTool Shell = new();
    private static readonly ReadFileTool Read = new();

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

    // What mcp-for-blender answers when a script fails in Blender: an ordinary result, not an MCP error.
    private const string BlenderError = """
        Error executing code: AttributeError: 'GeometryNodeTree' object has no attribute 'inputs'

        Traceback (most recent call last):
          File "C:\Repos\BlackBox\.dev\autopick\Data\tools\blender_mcp.py", line 2248, in execute_code
            exec(code, namespace)
          File "<string>", line 4, in <module>
        AttributeError: 'GeometryNodeTree' object has no attribute 'inputs'
        """;

    [Fact]
    public void An_error_naming_what_isnt_there_gets_a_note_at_once_and_coming_back_another()
    {
        var guesswork = new Guesswork();

        Assert.Equal(Guesswork.NameNote, guesswork.Check(Shell, ChartError(41)));
        Assert.Equal(Guesswork.RepeatNote, guesswork.Check(Shell, ChartError(44)));
        Assert.Equal(Guesswork.TriesNote(3), guesswork.Check(Shell, ChartError(47)));
    }

    [Fact]
    public void Other_errors_get_a_note_from_the_third_failure_since_the_last_look_up()
    {
        var guesswork = new Guesswork();

        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nAccess to the path 'C:\\Windows\\x.txt' is denied.")));
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\nall good"))); // what went fine never counts
        Assert.Null(guesswork.Check(Read, ToolResult.Error("File not found: C:\\x.txt")));
        Assert.Equal(Guesswork.TriesNote(3), guesswork.Check(Shell, ToolResult.Error("Exit code 2\nThe process cannot access the file because another process uses it.")));
        Assert.Equal(Guesswork.TriesNote(4), guesswork.Check(Shell, ToolResult.Error("Exit code 3\nThe operation timed out at the third step.")));

        // A look-up starts the count again; an error from before still counts as one seen.
        Assert.Null(guesswork.Check(new WebSearchTool(), new ToolResult("1. Some page\n   https://example.com/")));
        Assert.Null(guesswork.Check(Shell, ToolResult.Error("Exit code 4\nAnother thing went wrong.")));
        Assert.Equal(Guesswork.RepeatNote, guesswork.Check(Read, ToolResult.Error("File not found: C:\\x.txt")));
    }

    [Fact]
    public void Failures_long_ago_and_declined_calls_dont_count()
    {
        var guesswork = new Guesswork();
        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nFirst failure of the task.")));
        Assert.Null(guesswork.Check(Shell, ToolResult.Error("Exit code 1\nSecond failure of the task.")));
        for (var i = 0; i < 20; i++)
            Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\nfine")));
        Assert.Null(guesswork.Check(Shell, ToolResult.Error(AgentSession.DeclinedText)));
        Assert.Null(guesswork.Check(Shell, ToolResult.Error("Exit code 1\nThird failure, long after the others.")));
    }

    [Fact]
    public void The_first_failed_script_of_a_task_gets_a_note_unless_something_was_looked_up()
    {
        // Once in a task, and only for what runs code: a file that isn't there is no matter for a search.
        var guesswork = new Guesswork();
        Assert.Null(guesswork.Check(Read, ToolResult.Error("File not found: C:\\x.txt")));
        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));
        Assert.Null(guesswork.Check(new ShellTool(), new ToolResult("Exit code 0\nfine")));

        var searched = new Guesswork();
        Assert.Null(searched.Check(new FetchPageTool(), new ToolResult("# Docs")));
        Assert.Null(searched.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));
    }

    [Fact]
    public void A_tool_that_runs_code_has_its_errors_read_from_its_output()
    {
        var blender = new ToolResult(BlenderError);
        Assert.Equal("AttributeError: 'GeometryNodeTree' object has no attribute 'inputs'", Guesswork.ErrorLine(blender, errorsInText: true));
        Assert.True(Guesswork.NamesWhatIsNotThere(Guesswork.ErrorLine(blender, errorsInText: true)!));
        // A file that says "Error" is no failed call.
        Assert.Null(Guesswork.ErrorLine(blender));

        // Blender run with a script exits with 0 and prints the script's traceback among its own lines.
        var headless = new ToolResult("""
            Exit code 0
            Blender 5.2.2 (hash 8c3b8b2bb6e4 built 2026-09-29 01:38:00)
            Traceback (most recent call last):
              File "C:\Users\user\BlackBox\Data\scratch\scatter.py", line 7, in <module>
                tree.interface.new_socket("Size", in_out="INPUT", socket_type="NodeSocketFloatDistance")
            TypeError: NodeTreeInterface.new_socket(): error with keyword argument "socket_type" -  enum "NodeSocketFloatDistance" not found in ('NodeSocketBool', 'NodeSocketFloat')

            Blender quit
            """);
        var line = Guesswork.ErrorLine(headless, errorsInText: true)!;
        Assert.StartsWith("TypeError: NodeTreeInterface.new_socket()", line);
        Assert.True(Guesswork.NamesWhatIsNotThere(line));

        // How the server answered in a run: the add-on's error came back inside JSON, on one line.
        var json = new ToolResult("""
            Error executing code: Communication error with Blender: {"exception_type": "TypeError", "message": "bpy_struct: item.attr = val: NodeSocketInt.default_value expected an int type, not float", "traceback": "Traceback (most recent call last):\n  File \"C:\\Repos\\BlackBox\\.dev\\autopick\\Data\\tools\\blender_mcp.py\", line 2248, in execute_code\n    exec(code, namespace)\n    ~~~~^^^^^^^^^^^^^^^^^\n  File \"<string>\", line 54, in <module>\nTypeError: bpy_struct: item.attr = val: NodeSocketInt.default_value expected an int type, not float\n"}
            """);
        Assert.Equal("TypeError: bpy_struct: item.attr = val: NodeSocketInt.default_value expected an int type, not float", Guesswork.ErrorLine(json, errorsInText: true));

        // Every Python error starts with the same line; they are told apart by the one under the stack.
        var other = ToolResult.Error("Exit code 1\nTraceback (most recent call last):\n  File \"x.py\", line 1, in <module>\nZeroDivisionError: division by zero");
        Assert.NotEqual(Guesswork.Signature(headless, errorsInText: true), Guesswork.Signature(other));
    }

    [Theory]
    [InlineData("KeyError: 'bpy_prop_collection[key]: key \"Geometry\" not found'", true)]
    [InlineData("RuntimeError: Error: Node type GeometryNodeScatterPoints undefined", true)]
    [InlineData("TypeError: Mesh.from_pydata() got an unexpected keyword argument 'faces_list'", true)]
    [InlineData("error CS0117: 'KeyCode' does not contain a definition for 'Left'", true)]
    [InlineData("The term 'python' is not recognized as the name of a cmdlet, function, script file, or operable program.", true)]
    [InlineData("A parameter cannot be found that matches parameter name 'Recurse'.", true)]
    [InlineData("ffmpeg: Unrecognized option 'crf-max'.", true)]
    [InlineData("File not found: C:\\x.txt", false)]
    [InlineData("Access to the path 'C:\\Windows\\x.txt' is denied.", false)]
    [InlineData("ZeroDivisionError: division by zero", false)]
    [InlineData("RuntimeError: Operator bpy.ops.object.mode_set.poll() failed, context is incorrect", false)]
    [InlineData("AttributeError: 'NoneType' object has no attribute 'modifiers'", false)]
    [InlineData("TypeError: Cannot read properties of undefined (reading 'length')", false)]
    public void Errors_naming_what_isnt_there_are_told_apart(string line, bool missing) =>
        Assert.Equal(missing, Guesswork.NamesWhatIsNotThere(line));

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

    [Fact]
    public async Task A_script_an_MCP_server_reports_as_failed_gets_the_note()
    {
        var execute = new McpTool("blender", "blender__execute_blender_code", new ModelContextProtocol.Protocol.Tool { Name = "execute_blender_code" },
            (_, _) => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = BlenderError }] }));
        var server = new ScriptedServer(
            Sse.ToolCall("b1", "blender__execute_blender_code", """{"code":"tree.inputs.new('NodeSocketGeometry', 'Geometry')"}"""),
            Sse.Text("Done."));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), new ChatRequestOptions(),
            ToolRegistry.CreateDefault(includeScreenTools: false, extra: [execute]), ctx, "sys");

        await session.RunAsync("Build a geometry nodes graph in Blender", new RecordingObserver(), CancellationToken.None);

        Assert.EndsWith(Guesswork.NameNote, session.History.Single(m => m.Role == ChatRole.Tool).Content);
    }
}
