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
            Assert.Null(guesswork.Check(Read, new ToolResult("fine")));
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

    private static readonly WriteFileTool Write = new();
    private static readonly ToolResult Wrote = new("Wrote C:\\Users\\user\\BlackBox\\Data\\scratch\\motion.json");
    private static ChatMessage Reply(string thinking, string text = "") => new() { Role = ChatRole.Assistant, Reasoning = thinking, Content = text };

    [Theory]
    // What a model asks itself about how something is done, what it is called or whether it is there.
    [InlineData("Hmm, how do I make LTX 2.5 follow the motion of a reference video?", "Hmm, how do I make LTX 2.5 follow the motion of a reference video?")]
    [InlineData("Which node does the pose conditioning in the LTX workflow?", "Which node does the pose conditioning in the LTX workflow?")]
    [InlineData("Is there a node for depth control in LTX 2.5?", "Is there a node for depth control in LTX 2.5?")]
    [InlineData("But maybe they're built into newer ComfyUI?", "But maybe they're built into newer ComfyUI?")]
    [InlineData("What's the correct way to add a socket to a node group in Blender 5.2?", "What's the correct way to add a socket to a node group in Blender 5.2?")]
    [InlineData("I'm not sure how the IC-LoRA takes the control video.", "I'm not sure how the IC-LoRA takes the control video.")]
    [InlineData("The template runs. I don't know which sampler Nova Furry XL wants, so I'll keep euler.", "I don't know which sampler Nova Furry XL wants, so I'll keep euler.")]
    // From qwen3.8's thinking in earlier runs: the part of the sentence that asks.
    [InlineData("In ComfyUI, VAEDecode uses the VAE's dtype; is there a node input for fp32?", "is there a node input for fp32?")]
    [InlineData("Actually, does ComfyUI have a \"Remove Background\" node?", "Actually, does ComfyUI have a \"Remove Background\" node?")]
    [InlineData("I don't know if the original s3 was good or not.", null)]
    [InlineData("I need to check the node names in Blender 5.2. Let me do a quick web search for Blender 5.2's geometry nodes Python API. Actually, let me think about what I know.",
        "Let me do a quick web search for Blender 5.2's geometry nodes Python API.")]
    [InlineData("Let me search for the templates that make video first.", null)]
    [InlineData("Let me search the web for this specific problem.", null)]
    [InlineData("Let me search the web for this specific problem: \"Blender 5.2 modifier input not updating\".",
        "Let me search the web for this specific problem: \"Blender 5.2 modifier input not updating\".")]
    // A check it makes on this PC (inspect.signature on the installed moviepy), not on the web.
    [InlineData("Also, let's check the signature of crossfadein/crossfadeout in moviepy 2.x. Let me verify.", null)]
    [InlineData("Let me check the output folder for the new file.", null)]
    [InlineData("So how do we determine whether it's Juggernaut XL or SnowPony?", null)]
    [InlineData("Now, how does the film tool handle lip-sync?", null)]
    [InlineData("Hmm, write_videofile: does it have an audio parameter?", null)]
    // qwen3.8 in Excel, then answering itself wrongly: "No, the default is None, and Excel displays it."
    [InlineData("Also, maybe I should set delete=False on the x-axis (openpyxl's default axis delete is… in some versions, the default for the axis's `delete` attribute is True?",
        "Also, maybe I should set delete=False on the x-axis (openpyxl's default axis delete is… in some versions, the default for the axis's `delete` attribute is True?")]
    [InlineData("What does openpyxl's TextAxis.delete default to?", "What does openpyxl's TextAxis.delete default to?")]
    [InlineData("Does LoadAudio support flac files?", "Does LoadAudio support flac files?")]
    // Questions the web can't answer: the screen, the user, how a result looks, or nothing named.
    [InlineData("Did the click on the Export button work?", null)]
    [InlineData("How do I get to the button in the top right of the screen?", null)]
    [InlineData("What does the user want here, a portrait or a full body?", null)]
    [InlineData("How does the second render look?", null)]
    [InlineData("How should I proceed with this task now?", null)]
    [InlineData("Maybe I should crop just one view?", null)]
    [InlineData("I'm not sure which crop looks best.", null)]
    [InlineData("Let me make the frame first, then the shot.", null)]
    [InlineData("Wait, but how does the default value work now?", null)]
    public void Questions_about_how_things_are_done_are_told_apart(string thinking, string? question) =>
        Assert.Equal(question, Guesswork.QuestionIn(thinking));

    [Fact]
    public void A_question_the_model_goes_on_without_looking_up_gets_a_note_once_until_it_looks_something_up()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make the bunny dance like in dance.mp4");
        guesswork.Read(Reply("How do I make LTX follow the motion of a reference video? I'll try the depth guide first."), [Write]);
        Assert.Equal(Guesswork.QuestionNote, guesswork.Check(Write, Wrote));
        guesswork.Read(Reply("Which node does the pose conditioning in the LTX workflow?"), [Write]);
        Assert.Null(guesswork.Check(Write, Wrote));

        // It looks something up and reads what it found: a question then gets no note for a few calls.
        guesswork.Read(Reply("Let me search for LTX motion control."), [new WebSearchTool()]);
        Assert.Null(guesswork.Check(new WebSearchTool(), new ToolResult("1. Motion Control | LTX Documentation")));
        guesswork.Read(Reply("Is there a node for depth control in LTX 2.5?"), [Write]);
        Assert.Null(guesswork.Check(Write, Wrote));
        for (var i = 0; i < Guesswork.SearchGap; i++)
            Assert.Null(guesswork.Check(Read, new ToolResult("text")));
        guesswork.Read(Reply("Does LoadAudio support flac files?"), [Write]);
        Assert.Equal(Guesswork.QuestionNote, guesswork.Check(Write, Wrote));
    }

    [Theory]
    [InlineData(@"With Blender 5.2 (C:\Program Files\Blender Foundation\Blender 5.2\blender.exe), make scatter.blend", "Blender 5.2")]
    [InlineData("Make the bunny dance with LTX-2.5 like in dance.mp4", "LTX-2.5")]
    [InlineData("Use the latest moviepy 2.x for a title", "moviepy 2.x")]
    [InlineData("Make 3 cubes and a 6-second video", null)]
    public void The_program_a_request_names_is_found(string request, string? subject) =>
        Assert.Equal(subject, Guesswork.SubjectIn(request));

    [Fact]
    public void Going_on_past_an_error_it_was_told_to_look_up_makes_the_next_failure_say_so()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make scatter.blend with Blender 5.2");
        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));

        // It went on with another script: one that works needs no note, the next failure gets one.
        guesswork.Read(Reply("Let me try it another way."), [Shell]);
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0")));
        Assert.Equal(Guesswork.IgnoredNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nZeroDivisionError: division by zero")));

        // One that looks it up itself is left to it.
        var searching = new Guesswork();
        searching.StartRun("Make scatter.blend with Blender 5.2");
        Assert.Equal(Guesswork.FirstNote, searching.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));
        searching.Read(Reply("Let me search for it."), [new WebSearchTool()]);
        Assert.Null(searching.Check(new WebSearchTool(), new ToolResult("1. int() - Python documentation")));
        searching.Read(Reply("Now the script."), [Shell]);
        Assert.Null(searching.Check(Shell, ToolResult.Error("Exit code 1\nZeroDivisionError: division by zero")));

        // A command line's own slip is no matter for a search.
        var slip = new Guesswork();
        Assert.Equal(Guesswork.FirstNote, slip.Check(Shell, ToolResult.Error("Exit code 1\nUnexpected token 'version' in expression or statement.")));
        slip.Read(Reply("Let me quote it."), [Shell]);
        Assert.Null(slip.Check(Shell, ToolResult.Error("Exit code 1\nZeroDivisionError: division by zero")));
    }

    [Theory]
    // What Nemotron's Blender run failed with: the command line's own slips aren't ones to look up.
    [InlineData("Unexpected token 'version' in expression or statement.", false)]
    [InlineData("The filename, directory name, or volume label syntax is incorrect.", false)]
    [InlineData(@"ls : Cannot find path 'C:\Program' because it does not exist.", false)]
    [InlineData("Start-Process : A positional parameter cannot be found that accepts argument 'extension].enum_items])'.", false)]
    [InlineData("AttributeError: 'NodeTreeInterfaceSocketFloat' object has no attribute 'bl_idname'", true)]
    [InlineData("TypeError: 'bpy_prop_collection' object is not callable", true)]
    [InlineData("error CS0117: 'KeyCode' does not contain a definition for 'Left'", true)]
    [InlineData("Method invocation failed because [System.__ComObject] does not contain a method named 'Add'.", true)]
    public void Only_errors_a_search_answers_are_ones_to_look_up(string line, bool searchable) =>
        Assert.Equal(searchable, Guesswork.Searchable(line));

    [Fact]
    public void A_try_the_model_calls_wrong_counts_as_a_failed_one()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make the bunny dance like in dance.mp4");
        Assert.Null(guesswork.Check(Write, Wrote));

        // Nothing failed; the video just didn't do what was asked. Its note comes with the next result.
        guesswork.Read(Reply("The video still doesn't follow the dance. Let me raise the guide strength."), [Write]);
        Assert.Equal(Guesswork.DoubtNote, guesswork.Check(Write, Wrote));
        // Said once; then, as for errors, from the third failure since the last look-up.
        guesswork.Read(Reply("No change in the motion at all."), [Write]);
        Assert.Null(guesswork.Check(Write, Wrote));
        guesswork.Read(Reply("That didn't work either; I need a different approach."), [Write]);
        Assert.Equal(Guesswork.TriesNote(3), guesswork.Check(Write, Wrote));

        // Words of doubt with no try before them, or in a reply that looks something up, don't count.
        var fresh = new Guesswork();
        fresh.StartRun("Make the bunny dance");
        fresh.Read(Reply("If this still doesn't work, I'll look it up."), [Write]);
        Assert.Null(fresh.Check(Write, Wrote));
        fresh.Read(Reply("It still isn't moving."), [new WebSearchTool()]);
        Assert.Null(fresh.Check(new WebSearchTool(), new ToolResult("1. LTX motion control")));
        fresh.Read(Reply("It still isn't moving."), [Write]);
        Assert.Null(fresh.Check(Write, Wrote)); // looked up in this task: one more failure is no matter yet
    }

    [Fact]
    public void The_user_saying_the_result_isnt_right_counts_as_a_failed_try()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make a picture of Anton from his refsheet");
        Assert.Null(guesswork.Check(Write, Wrote));

        guesswork.StartRun("Dude it's still a kid, not an adult");
        Assert.Equal(Guesswork.UserDoubtNote, guesswork.Check(new ReadFileTool(), new ToolResult("refsheet.png")));

        // What a video does wrong, in the model's words after a look or a measurement.
        Assert.True(Guesswork.SaysItFailed("The measurement says the ball moves right to left, the opposite of what was asked."));
        Assert.True(Guesswork.SaysItFailed("The bunny barely moves in the whole clip."));
        Assert.True(Guesswork.SaysItFailed("The ball rolls the wrong way."));
        Assert.False(Guesswork.SaysItFailed("The ball rolls from left to right, as asked, and the camera stays still."));

        // Saying what it should have done is saying it isn't right.
        Assert.True(Guesswork.UserDoubts("The text fades in from black; it should fade from transparent, with the blue showing through."));
        Assert.False(Guesswork.UserDoubts("Great, now make a version 10 seconds long."));

        // Without a try before it, a message is a new task, whatever it says.
        var fresh = new Guesswork();
        fresh.StartRun("My printer doesn't work, fix it");
        Assert.Null(fresh.Check(Shell, new ToolResult("Exit code 0\nPrinter: HP LaserJet")));
    }

    [Fact]
    public void Ten_minutes_of_tries_with_nothing_looked_up_makes_every_next_try_say_to_look()
    {
        var now = new DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc);
        var guesswork = new Guesswork(() => now);
        guesswork.StartRun("Make the bunny dance like in dance.mp4");
        for (var i = 0; i < 3; i++)
        {
            now += TimeSpan.FromMinutes(3);
            Assert.Null(guesswork.Check(Write, Wrote));
        }
        now += TimeSpan.FromMinutes(2);
        Assert.Equal(Guesswork.CheckNote(TimeSpan.FromMinutes(11), 4), guesswork.Check(Write, Wrote));
        Assert.Null(guesswork.Check(new ReadFileTool(), new ToolResult("text"))); // only tries say it
        now += TimeSpan.FromMinutes(1);
        Assert.Equal(Guesswork.CheckNote(TimeSpan.FromMinutes(12), 5), guesswork.Check(Write, Wrote));

        // A look-up starts the clock again, and so does reading the installed version's own API.
        Assert.Null(guesswork.Check(new FetchPageTool(), new ToolResult("# Motion control")));
        now += TimeSpan.FromMinutes(5);
        Assert.Null(guesswork.Check(Write, Wrote));
        for (var i = 0; i < 2; i++)
        {
            now += TimeSpan.FromMinutes(2);
            Assert.Null(guesswork.Check(Write, Wrote));
        }
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\n(self, duration: float, initial_color: list = None)"),
            """{"command":"python -c \"import inspect; from moviepy.video.fx import FadeIn; print(inspect.signature(FadeIn.__init__))\""}"""));
        now += TimeSpan.FromMinutes(1);
        Assert.Null(guesswork.Check(Write, Wrote));

        // Time between runs, while the user is away, isn't time spent trying.
        now += TimeSpan.FromHours(3);
        guesswork.StartRun("Now make it longer");
        now += TimeSpan.FromMinutes(1);
        Assert.Null(guesswork.Check(Write, Wrote));
        Assert.Null(guesswork.Check(Write, Wrote));
    }

    [Fact]
    public void Eight_quick_tries_with_nothing_looked_up_make_it_time_to_look_too()
    {
        var now = new DateTime(2026, 10, 21, 8, 34, 0, DateTimeKind.Utc);
        var guesswork = new Guesswork(() => now);
        guesswork.StartRun("Make scatter.blend with Blender 5.2");
        for (var i = 1; i < Guesswork.CheckBurst; i++)
        {
            now += TimeSpan.FromSeconds(20);
            Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\nprobing")));
        }
        now += TimeSpan.FromSeconds(20);
        Assert.Equal(Guesswork.CheckNote(TimeSpan.FromSeconds(160), 8), guesswork.Check(Shell, new ToolResult("Exit code 0\nprobing")));
        Assert.StartsWith("[BlackBox] Time to look things up: 8 tries and 2 minutes since", Guesswork.CheckNote(TimeSpan.FromSeconds(160), 8));
    }

    [Fact]
    public void Looking_things_up_for_long_with_no_try_says_to_ask_the_installed_version()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make scatter.blend with Blender 5.2");
        var page = new ToolResult("# nodeitems_builtins.py");
        for (var i = 1; i < Guesswork.LongSearch; i++)
            Assert.Null(guesswork.Check(new FetchPageTool(), page));
        Assert.Equal(Guesswork.LongSearchNote(8), guesswork.Check(new FetchPageTool(), page));
        for (var i = 9; i < 14; i++)
            Assert.Null(guesswork.Check(new WebSearchTool(), page));
        Assert.Equal(Guesswork.LongSearchNote(14), guesswork.Check(new FetchPageTool(), page));

        // A try starts the count again.
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\n['GeometryNodeGroup', 'NodeGroupInput', 'NodeGroupOutput']")));
        for (var i = 1; i < Guesswork.LongSearch; i++)
            Assert.Null(guesswork.Check(new FetchPageTool(), page));
        // So does asking the installed version, as the note says to, and the user's next message.
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\nHelp on module bpy"), """{"command": "blender -b --python-expr \"help(bpy.types)\""}"""));
        for (var i = 1; i < Guesswork.LongSearch; i++)
            Assert.Null(guesswork.Check(new FetchPageTool(), page));
        guesswork.StartRun("Now add a camera to it");
        for (var i = 1; i < Guesswork.LongSearch; i++)
            Assert.Null(guesswork.Check(new FetchPageTool(), page));
    }

    [Fact]
    public void A_request_that_is_all_research_gets_no_note_to_ask_the_installed_version()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Compare these three laptops and tell me which is best for video editing");
        for (var i = 0; i < 20; i++)
            Assert.Null(guesswork.Check(i % 2 == 0 ? new WebSearchTool() : new FetchPageTool(), new ToolResult("A review")));
    }

    [Theory]
    // The search it means to make, not what came before its colon (which named the screen).
    [InlineData("The modifier value on screen did not change: let me search the web for Blender 5.2 geometry nodes modifier input python.",
                "let me search the web for Blender 5.2 geometry nodes modifier input python")]
    public void A_search_the_model_means_to_make_is_found_after_what_came_before_it(string thinking, string expected) =>
        Assert.Equal(expected, Guesswork.QuestionIn(thinking)?.TrimEnd('.'));

    [Theory]
    [InlineData("run", true)]
    [InlineData("templates", false)]
    [InlineData("nodes", false)]
    [InlineData("status", false)]
    public void Only_a_comfyui_job_is_a_try(string action, bool isTry) =>
        Assert.Equal(isTry, Guesswork.IsTry(new ComfyTool(), Guesswork.ActionOf($$"""{"action": "{{action}}"}""")));

    [Fact]
    public async Task In_a_run_a_question_the_model_goes_on_with_gets_a_note_and_nothing_is_searched_for_it()
    {
        var server = new ScriptedServer(
            Sse.Thought("How do I make LTX follow the motion of a reference video? I'll try the depth guide first.",
                Sse.ToolCall("w1", "write_file", """{"path":"motion.json","content":"{}"}""")),
            Sse.Thought("Hmm, which node does the pose conditioning in the LTX workflow?",
                Sse.ToolCall("w2", "write_file", """{"path":"motion.json","content":"{}"}""")),
            Sse.Text("Done."));
        var dir = Directory.CreateTempSubdirectory().FullName;
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = dir };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), new ChatRequestOptions(),
            ToolRegistry.CreateDefault(includeScreenTools: false, includeWebTools: true), ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("Make the bunny dance like in dance.mp4", observer, CancellationToken.None);

        // The note says to search; it neither quotes the model's thinking nor brings search results.
        var results = session.History.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content!).ToList();
        Assert.EndsWith("\n" + Guesswork.QuestionNote, results[0]);
        Assert.DoesNotContain("LTX", results[0]);
        Assert.DoesNotContain(Guesswork.QuestionNote, results[1]);
        Assert.DoesNotContain(observer.Notices, n => n.Contains("Looked up"));
    }
}
