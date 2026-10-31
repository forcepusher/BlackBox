using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Projects;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

// index_folder: the index of a folder (what it holds, what its files define, the words of each
// part), its overview, its search, and the note that says to index a project first.
public class FolderIndexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-index-").FullName;
    private readonly string _store;

    public FolderIndexTests()
    {
        _store = Path.Combine(_dir, "store");
        FolderIndex.ForgetLoaded();
    }

    public void Dispose()
    {
        FolderIndex.ForgetLoaded();
        Directory.Delete(_dir, recursive: true);
    }

    private ToolContext Ctx(string? store = null) =>
        new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir, IndexDirectory = store ?? _store };

    private string Write(string path, string text)
    {
        var full = Path.Combine(_dir, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static async Task<ToolResult> Run(ToolContext ctx, string path, string? query = null)
    {
        var args = new JsonObject { ["path"] = path };
        if (query is not null)
            args["query"] = query;
        var result = await new IndexFolderTool().ExecuteAsync(args, ctx, CancellationToken.None);
        return result with { Text = result.Text.ReplaceLineEndings("\n") };
    }

    /// <summary>The Sloperator repository these tests run in.</summary>
    private static string Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles("Sloperator.slnx").Any())
                return dir.FullName;
        throw new InvalidOperationException("Not run from the repository.");
    }

    /// <summary>Questions about Sloperator's own code and the file that answers each, as the model might ask them.</summary>
    internal static readonly (string Query, string File)[] RepositoryQuestions =
    [
        ("where the settings are saved to disk", "AppSettings.cs"),
        ("a sharp enlarged picture of part of the screen with rulers", "ZoomTool.cs"),
        ("Pause/Break stops the agent", "WindowsStopKey.cs"),
        ("handoff note when the context is full", "AgentSession.Handoff.cs"),
        ("second start hands off to the running instance over a named pipe", "SingleInstance.cs"),
        ("DuckDuckGo search results", "WebSearch.cs"),
        ("capture the screen with GDI", "WindowsScreenCapture.cs"),
        ("repair broken JSON in tool calls", "ToolCallRepair.cs"),
        ("prompt caching for Claude", "AnthropicMessages.cs"),
        ("saved chats on disk", "ChatStore.cs"),
        ("ssh host keys known_hosts", "SshHosts.cs"),
        ("install ComfyUI portable", "ComfyInstaller.cs"),
        ("storyboard shots and frames", "Storyboard.cs"),
        ("pick the strongest model the server has", "ModelChoice.cs"),
        ("insert a row in an Excel sheet and move the formulas", "SheetEdit.cs"),
        ("Markdown to a Word document", "DocxWriter.cs"),
        ("headless Edge browser", "HeadlessBrowser.cs"),
        ("the same tool call repeated in a loop", "LoopDetector.cs"),
        ("windows take turns with the mouse and keyboard", "DesktopTurn.cs"),
        ("text of a web page without menus", "PageText.cs"),
        ("song judged by SongEval", "SongJudge.cs"),
        ("Kilo free models gateway", "KiloGateway.cs"),
        ("MouseClickTool", "DesktopTools.cs"),
        ("SettingsStore", "AppSettings.cs"),
        ("ResetContextAsync", "AgentSession.Handoff.cs"),
        ("McpHost", "McpHost.cs"),
    ];

    // Not a test: what the model gets for this repository, written to BB_INDEX_PROBE to read.
    [Fact]
    public async Task Probe_the_repository()
    {
        if (Environment.GetEnvironmentVariable("BB_INDEX_PROBE") is not { Length: > 0 } output)
            return;
        // Another folder, with its own questions: BB_INDEX_PROBE_ROOT, and BB_INDEX_PROBE_QUERIES split by "|".
        if (Environment.GetEnvironmentVariable("BB_INDEX_PROBE_ROOT") is { Length: > 0 } other)
        {
            var report = new StringBuilder();
            var clock = Stopwatch.StartNew();
            var first = await Run(Ctx(), other);
            report.AppendLine($"=== overview ({clock.ElapsedMilliseconds} ms, {first.Text.Length} chars)").AppendLine(first.Text);
            clock.Restart();
            report.AppendLine($"=== again ({clock.ElapsedMilliseconds} ms)").AppendLine((await Run(Ctx(), other)).Text.Split('\n')[0]);
            foreach (var file in Directory.GetFiles(_store))
                report.AppendLine($"store: {Path.GetFileName(file)} {new FileInfo(file).Length:N0} bytes");
            foreach (var query in (Environment.GetEnvironmentVariable("BB_INDEX_PROBE_QUERIES") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                clock.Restart();
                var found = await Run(Ctx(), other, query);
                report.AppendLine($"=== {query} ({clock.ElapsedMilliseconds} ms, {found.Text.Length} chars)").AppendLine(found.Text);
            }
            File.WriteAllText(output, report.ToString());
            return;
        }
        var repo = Repository();
        var sb = new StringBuilder();
        var watch = Stopwatch.StartNew();
        var overview = await Run(Ctx(), repo);
        sb.AppendLine($"=== overview ({watch.ElapsedMilliseconds} ms, {overview.Text.Length} chars)").AppendLine(overview.Text);
        watch.Restart();
        var again = await Run(Ctx(), repo);
        sb.AppendLine($"=== again ({watch.ElapsedMilliseconds} ms)").AppendLine(again.Text.Split('\n')[0]);
        FolderIndex.ForgetLoaded();
        watch.Restart();
        var fromDisk = await Run(Ctx(), repo);
        sb.AppendLine($"=== from disk ({watch.ElapsedMilliseconds} ms)").AppendLine(fromDisk.Text.Split('\n')[0]);
        foreach (var file in Directory.GetFiles(_store))
            sb.AppendLine($"store: {Path.GetFileName(file)} {new FileInfo(file).Length:N0} bytes");
        var (index, _) = await FolderIndex.OpenAsync(repo, _store, null, CancellationToken.None);
        foreach (var (query, expected) in RepositoryQuestions)
        {
            watch.Restart();
            var found = await Run(Ctx(), Path.Combine(repo, "src"), query);
            sb.AppendLine($"=== {query} -> {expected}: rank {FileRank(index, query, "src", expected)} ({watch.ElapsedMilliseconds} ms, {found.Text.Length} chars)").AppendLine(found.Text);
        }
        sb.AppendLine("=== ranks: " + string.Join(" ", RepositoryQuestions.Select(q => FileRank(index, q.Query, "src", q.File))));
        File.WriteAllText(output, sb.ToString());
    }

    /// <summary>Where the file named <paramref name="file"/> first comes among the files of the search's parts (1 = first), or 0 when not in the first ten.</summary>
    private static int FileRank(FolderIndex index, string query, string under, string file)
    {
        var files = IndexSearch.Rank(index, query, under).Select(h => h.File).Distinct().Take(10).ToList();
        return files.FindIndex(f => Path.GetFileName(f.Path).Equals(file, StringComparison.OrdinalIgnoreCase)) + 1;
    }

    [Fact]
    public void Names_split_into_words_and_words_lose_their_endings()
    {
        Assert.Equal(["Save", "Settings"], Terms.Parts("SaveSettings"));
        Assert.Equal(["save", "settings", "path"], Terms.Parts("save_settings_path"));
        Assert.Equal(["HTTP", "Server", "2"], Terms.Parts("HTTPServer2"));
        Assert.Equal(["Rename"], Terms.Parts("Rename"));

        Assert.All(new[] { "saved", "saving", "saves", "save" }, w => Assert.Equal("sav", Terms.Stem(w)));
        Assert.All(new[] { "settings", "setting", "set" }, w => Assert.Equal("set", Terms.Stem(w)));
        Assert.All(new[] { "indexes", "indexing", "indexed", "index" }, w => Assert.Equal("index", Terms.Stem(w)));
        Assert.Equal("status", Terms.Stem("status"));
        Assert.Equal("fall", Terms.Stem("falling"));

        // A name is found whole and by its words; a question's empty words are dropped.
        Assert.Equal(["savesettings", "sav", "set"], Terms.Of("SaveSettings"));
        Assert.Equal(["settings", "set", "saved", "sav"], Terms.OfQuery("where are the settings saved"));
        Assert.True(Terms.IsName("who calls SaveSettings", "savesettings"));
        Assert.True(Terms.IsName("save_game slots", "save_game"));
        Assert.False(Terms.IsName("where are the settings saved", "settings"));
    }

    [Fact]
    public void Definitions_are_found_in_each_language()
    {
        static List<string> Find(string language, string code) =>
            Symbols.Find(language, code.ReplaceLineEndings("\n").Split('\n')).Select(s => $"{s.Kind} {s.Name} {s.Line}").ToList();

        Assert.Equal(
            ["namespace Game.Core 1", "class SettingsStore 3", "property Path 5", "method Save 7", "method Has 12", "record Slot 14", "enum Mode 15"],
            Find("C#", """
                namespace Game.Core;

                public sealed partial class SettingsStore(string folder) : IDisposable
                {
                    public string Path { get; } = folder;
                    private static readonly Regex Name = new(@"\w+");
                    public async Task Save(Settings settings, CancellationToken ct)
                    {
                        var result = new ToolResult(text,
                            look.Image);
                    }
                    internal static bool Has(string path) => File.Exists(path);
                    public abstract Task<string> Load(string path);
                    public sealed record Slot(int Number);
                    public enum Mode { One, Two }
                }
                """));
        Assert.Equal(["class Player 1", "method __init__ 2", "function spawn 5"],
            Find("Python", "class Player(Base):\n    def __init__(self):\n        pass\n\nasync def spawn(world):\n    if ready():\n        pass"));
        Assert.Equal(["class Enemy 1", "method update 2", "function makeLevel 6", "function onKey 7"],
            Find("TypeScript", "export class Enemy extends Actor {\n  update(dt: number): void {\n    if (x) {\n  }\n}\nexport function makeLevel(n) {}\nconst onKey = async (e) => {};"));
        Assert.Equal(["function main 1", "struct Board 2"], Find("Go", "func main() {\ntype Board struct {"));
        Assert.Equal(["impl Board 1", "method new 2"], Find("Rust", "impl Board {\n    pub fn new() -> Self {"));
        Assert.Equal(["class Hero 1", "function _ready 2", "signal died 3"], Find("GDScript", "class_name Hero\nfunc _ready():\nsignal died"));
        // A heading inside a code block is a comment of the code it shows.
        Assert.Equal(["heading Install 1", "heading Build it 5"], Find("Markdown", "# Install\n```sh\n# not a heading\n```\n## Build it"));
    }

    [Fact]
    public void Gitignore_rules_and_the_folders_tools_make_are_left_out()
    {
        Write(".gitignore", "*.log\n!keep.log\nbuild/\n/docs/private\n# a comment\n**/cache/**\n");
        Write("Game.csproj", "<Project />");
        foreach (var file in new[]
                 {
                     "bin/Debug/Game.dll", "obj/Game.cs", "src/Player.cs", "src/debug.log", "keep.log", "build/out.txt", "node_modules/m/index.js",
                     "docs/private/secret.md", "docs/public.md", "src/build/Builder.cs", "a/cache/b/c.txt", "unity/Assets/Hero.cs",
                     "unity/ProjectSettings/ProjectVersion.txt", "unity/Library/big.asset", "venv/pyvenv.cfg", "venv/lib/site.py",
                 })
            Write(file, "x");

        var (files, walk) = FolderWalk.Run(_dir, CancellationToken.None);
        var found = files.Select(f => f.Path).Order(StringComparer.Ordinal).ToList();

        Assert.Equal([".gitignore", "Game.csproj", "docs/public.md", "keep.log", "src/Player.cs", "unity/Assets/Hero.cs", "unity/ProjectSettings/ProjectVersion.txt"], found);
        Assert.Contains("bin (build output)", walk.LeftOut.Keys);
        Assert.Contains("Library (Unity's own)", walk.LeftOut.Keys);
        Assert.Contains("node_modules", walk.LeftOut.Keys);
        Assert.Contains("venv (a Python environment)", walk.LeftOut.Keys);
    }

    [Fact]
    public void A_junction_is_not_walked_and_a_linked_file_has_its_targets_size()
    {
        var project = Path.Combine(_dir, "proj");
        Write("proj/src/Player.cs", "class Player {}");
        var big = Write("elsewhere/big.txt", new string('x', 5000));
        // A junction back to the project itself: walked, it would go round for ever.
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(project, "loop")}\" \"{project}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(project, "loop")));
        var linked = true;
        try { File.CreateSymbolicLink(Path.Combine(project, "big-link.txt"), big); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { linked = false; } // symbolic links need Developer Mode

        var (files, _) = FolderWalk.Run(project, CancellationToken.None);

        Assert.DoesNotContain(files, f => f.Path.StartsWith("loop", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Path == "src/Player.cs");
        if (linked)
            Assert.Equal(5000, files.Single(f => f.Path == "big-link.txt").Size);
        Directory.Delete(Path.Combine(project, "loop")); // the junction alone, not what it points to
    }

    [Fact]
    public async Task An_index_is_kept_up_to_date_and_read_back_from_its_file()
    {
        var project = Path.Combine(_dir, "game");
        Write("game/src/Player.cs", "public class Player\n{\n    public void Jump() => Velocity.Y = JumpSpeed;\n}\n");
        Write("game/src/Enemy.cs", "public class Enemy\n{\n    public void Chase(Player target) { }\n}\n");
        Write("game/README.md", "# Jumper\nA platform game.\n");

        var built = await Run(Ctx(), project);
        Assert.False(built.IsError);
        Assert.StartsWith($"Indexed {project}: 3 files, 3 of them read", built.Text);
        Assert.Contains("Built in", built.Text);
        Assert.Single(Directory.GetFiles(_store));

        // One changed, one new, one gone: only those are read again.
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Write("game/src/Player.cs", "public class Player\n{\n    public void Jump() => Velocity.Y = JumpSpeed;\n    public void DoubleJump() { }\n}\n");
        File.SetLastWriteTimeUtc(Path.Combine(project, "src", "Player.cs"), DateTime.UtcNow.AddMinutes(1));
        Write("game/src/Coin.cs", "public class Coin { public int Value = 10; }\n");
        File.Delete(Path.Combine(project, "src", "Enemy.cs"));
        var updated = await Run(Ctx(), project);
        Assert.Contains("Brought up to date: 1 new, 1 changed, 1 gone files since the last time", updated.Text);
        Assert.Contains("DoubleJump", (await Run(Ctx(), project, "DoubleJump")).Text);
        Assert.StartsWith($"No file in {project} has any of these words: Chase.", (await Run(Ctx(), project, "Chase")).Text);

        // A new process finds it in its file, up to date.
        FolderIndex.ForgetLoaded();
        var again = await Run(Ctx(), project);
        Assert.Contains("Up to date", again.Text);
        Assert.Contains("Coin.cs", (await Run(Ctx(), project, "coin value")).Text);
    }

    [Fact]
    public async Task Names_are_found_by_their_parts_and_by_names_that_are_also_plain_words()
    {
        var project = Path.Combine(_dir, "tools");
        Write("tools/src/Clicks.cs", "public sealed class MouseClickTool\n{\n    public Tool? Find(string name) => null;\n}\n");
        Write("tools/src/Other.cs", "public class Other { }\n");
        await Run(Ctx(), project);

        Assert.Contains("1. " + Path.Combine(project, "src", "Clicks.cs"), (await Run(Ctx(), project, "MouseClick")).Text);
        // "find" says nothing in a question, but a query of it is for the method.
        Assert.Contains("- method Find: " + Path.Combine(project, "src", "Clicks.cs") + ":3", (await Run(Ctx(), project, "Find")).Text);
        Assert.Equal(["settings", "saved"], Terms.Words("where are the settings saved"));
        Assert.Equal(["Find", "the"], Terms.Words("Find the"));
    }

    [Fact]
    public async Task A_search_in_a_project_uses_its_own_index_not_one_of_a_folder_above_it()
    {
        var project = Path.Combine(_dir, "inner");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Write("inner/src/Engine.cs", "public class Engine { public void Start() { } }\n");
        Write("notes.txt", "Engine notes\n");
        await Run(Ctx(), _dir); // the folder the project is in, indexed once

        Assert.StartsWith($"(Indexed {project} for this search", (await Run(Ctx(), Path.Combine(project, "src"), "Engine")).Text);
        // A search of a part of it then goes to the project's index.
        var again = (await Run(Ctx(), Path.Combine(project, "src"), "Engine")).Text;
        Assert.DoesNotContain("(Indexed", again);
        Assert.Contains($"Best matches in {Path.Combine(project, "src")}", again);
    }

    [Fact]
    public async Task A_file_in_use_is_read_once_it_is_free_and_an_odd_package_file_is_no_matter()
    {
        var project = Path.Combine(_dir, "busy");
        Write("busy/src/Main.cs", "public class Main { }\n");
        Write("busy/package.json", "[]");
        var held = Write("busy/src/Held.cs", "public class Held { public void Wobble() { } }\n");

        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var busy = await Run(Ctx(), project);
            Assert.False(busy.IsError, busy.Text);
            Assert.Contains("1 that couldn't be opened (in use or not allowed; tried again next time)", busy.Text);
            Assert.Contains("package.json: not a package's description", busy.Text);
        }
        Assert.Contains("Held.cs", (await Run(Ctx(), project, "Wobble")).Text);
    }

    [Fact]
    public async Task An_index_kept_inside_the_folder_it_indexes_is_left_out_of_it()
    {
        var project = Path.Combine(_dir, "portable");
        Write("portable/src/App.cs", "public class App { }\n");
        var store = Path.Combine(project, "Data", "index");

        await Run(Ctx(store), project);
        var again = (await Run(Ctx(store), project)).Text;
        Assert.Contains("Up to date", again);
        Assert.Contains("index (Sloperator's indexes)", again);
    }

    [Fact]
    public async Task A_search_of_a_part_of_an_indexed_project_counts_as_indexing_it()
    {
        var project = Path.Combine(_dir, "part");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        for (var i = 0; i < ProjectNudge.MinFiles; i++)
            Write($"part/src/F{i}.cs", "class C {}");
        Write("part/README.md", "# Part\n");
        var ctx = Ctx();
        await Run(ctx, project);

        var nudge = new ProjectNudge();
        Assert.Null(nudge.Check(new IndexFolderTool(), new JsonObject { ["path"] = Path.Combine(project, "src"), ["query"] = "C" }.ToJsonString(), new ToolResult("Best matches"), ctx));
        Assert.Null(nudge.Check(new ReadFileTool(), new JsonObject { ["path"] = Path.Combine(project, "README.md") }.ToJsonString(), new ToolResult("1\t# Part"), ctx));
    }

    [Fact]
    public async Task Windows_that_index_and_search_one_folder_at_once_each_get_their_answer()
    {
        var project = Path.Combine(_dir, "shared");
        for (var i = 0; i < 200; i++)
            Write($"shared/src/Part{i}.cs",$"public class Part{i}\n{{\n    public int Weight => {i};\n}}\n");
        // Files change while the searches run, so each brings the index up to date as another reads it.
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                Write($"shared/src/Added{i}.cs", $"public class Added{i} {{ }}\n");
        }, TestContext.Current.CancellationToken);
        var calls = Enumerable.Range(0, 24).Select(i => Task.Run(() => Run(Ctx(), project, i % 3 == 0 ? null : $"Part{i} weight"), TestContext.Current.CancellationToken)).ToList();
        var results = await Task.WhenAll(calls);
        await writer;

        Assert.All(results, r => Assert.False(r.IsError, r.Text));
        Assert.All(results.Where((_, i) => i % 3 != 0), r => Assert.Contains("Best matches", r.Text));
    }

    [Fact]
    public async Task Search_finds_the_part_a_question_is_about_and_where_a_name_is_defined()
    {
        var project = Path.Combine(_dir, "app");
        Write("app/src/Settings.cs", """
            using System.Text.Json;

            /// <summary>Keeps the user's choices between runs.</summary>
            public sealed class SettingsStore(string path)
            {
                public Settings Load() => JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))!;

                /// <summary>Writes them to disk, all at once.</summary>
                public void Save(Settings settings)
                {
                    var json = JsonSerializer.Serialize(settings);
                    File.WriteAllText(path, json);
                }
            }
            """);
        for (var i = 0; i < 12; i++)
            Write($"app/src/Level{i}.cs", $"public class Level{i}\n{{\n    public void Spawn(Enemy enemy) {{ enemy.Health = {i * 10}; }}\n    public void Draw(Canvas canvas) {{ canvas.Clear(); }}\n}}\n");
        Write("app/tests/SettingsTests.cs", "public class SettingsTests\n{\n    public void Settings_are_saved() { new SettingsStore(path).Save(settings); }\n}\n");

        // Searched before it was indexed, it is indexed for the search.
        Assert.StartsWith($"(Indexed {project} for this search: 14 files.", (await Run(Ctx(), project, "Spawn")).Text);
        var question = (await Run(Ctx(), project, "where are the settings saved to disk")).Text;
        Assert.True(question.StartsWith($"Best matches in {project} for \"where are the settings saved to disk\":\n1. {Path.Combine(project, "src", "Settings.cs")}, lines "), question);
        Assert.DoesNotContain("Defined:", question); // "settings" in a question isn't a name
        Assert.Contains(": public void Save(Settings settings)", question);
        Assert.Contains("read_file with offset", question);

        var name = (await Run(Ctx(), project, "SettingsStore")).Text;
        Assert.StartsWith($"Defined:\n- class SettingsStore: {Path.Combine(project, "src", "Settings.cs")}:4\n", name);

        // A folder inside the index limits the search to it.
        var inTests = (await Run(Ctx(), Path.Combine(project, "tests"), "SettingsStore save")).Text;
        Assert.Contains($"Best matches in {Path.Combine(project, "tests")}", inTests);
        Assert.DoesNotContain("src", inTests.Replace(project, ""));

        Assert.Contains("No file in", (await Run(Ctx(), project, "zeppelin")).Text);
    }

    [Fact]
    public async Task A_search_for_a_name_lists_every_line_that_has_it()
    {
        var project = Path.Combine(_dir, "car");
        Write("car/src/Engine.cs", "public class Engine\n{\n    public void Start() { }\n}\n");
        Write("car/src/Car.cs", "public class Car\n{\n    private readonly Engine _engine = new();\n    public void Drive() => _engine.Start();\n}\n");
        Write("car/src/EngineRoom.cs", "public class EngineRoom { }\n");
        Write("car/tests/CarTests.cs", "public class CarTests\n{\n    public void Starts() => new Engine().Start();\n}\n");

        // Every use, as a whole word: what an interface's implementations or a rename need, which the best parts alone aren't.
        var found = (await Run(Ctx(), project, "Engine")).Text;
        Assert.Contains($"Every line with Engine, 3 in 3 file(s) under {project}:\n", found);
        Assert.Contains(Path.Combine("src", "Car.cs") + ":3: private readonly Engine _engine = new();\n", found);
        Assert.Contains(Path.Combine("tests", "CarTests.cs") + ":3: public void Starts() => new Engine().Start();\n", found);
        Assert.DoesNotContain("EngineRoom.cs:", found);
        Assert.Contains("Best matches", found);

        // A plain word of a question isn't a name to list.
        Assert.DoesNotContain("Every line", (await Run(Ctx(), project, "how does the engine start")).Text);

        // Many: each file's first such line, and where the others are.
        for (var i = 0; i < 30; i++)
            Write($"car/src/Part{i}.cs", $"public class Part{i}\n{{\n    public void Fit(Engine engine) {{ }}\n    public void Tune(Engine engine) {{ }}\n}}\n");
        var many = (await Run(Ctx(), project, "Engine")).Text;
        Assert.Contains("Every line with Engine, 63 in 33 file(s)", many);
        Assert.Contains(Path.Combine("src", "Part1.cs") + ":3: public void Fit(Engine engine) { } (and line 4)\n", many);
        Assert.Contains($"and {33 - IndexSearch.MaxUseFiles} more files", many);
    }

    [Fact]
    public async Task The_overview_says_what_the_project_is_and_what_its_files_define()
    {
        var project = Path.Combine(_dir, "web");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Write("web/.git/HEAD", "ref: refs/heads/main\n");
        Write("web/package.json", """{ "name": "shop", "scripts": { "dev": "vite", "build": "vite build" }, "dependencies": { "react": "^19" } }""");
        Write("web/README.md", "[![build](https://ci/badge.svg)](https://ci)\n# Shop\n\nAn online shop for board games.\n");
        Write("web/AGENTS.md", "Use pnpm, not npm.\n");
        Write("web/src/cart.ts", "export class Cart {\n  addItem(item: Item): void {\n  }\n}\nexport function checkout(cart: Cart) {}\n");
        Write("web/src/app.tsx", "import { Cart, checkout } from './cart';\nexport default function App() { const cart = new Cart(); checkout(cart); }\n");
        Write("web/public/logo.png", "\0PNG");

        var overview = (await Run(Ctx(), project)).Text;
        Assert.Contains("A git repository, on branch main.", overview);
        Assert.Contains("package.json: Node package shop; npm scripts dev, build; depends on react.", overview);
        Assert.Contains("# Shop\nAn online shop for board games.", overview);
        Assert.DoesNotContain("badge", overview);
        Assert.Contains("Notes for AI agents in the project:\n(AGENTS.md, 1 line, all of it)\nUse pnpm, not npm.", overview);
        Assert.Contains("TypeScript 2 files", overview);
        Assert.Contains($"src{Path.DirectorySeparatorChar} 2", overview);
        Assert.Contains($"src{Path.DirectorySeparatorChar}cart.ts (5 lines): class Cart (addItem); functions checkout", overview);
        Assert.Contains($"src{Path.DirectorySeparatorChar}app.tsx (2 lines): App", overview);
        Assert.Contains("Listed but not read: 1 pictures", overview);
        Assert.EndsWith($"To find anything in it: index_folder path=\"{project}\" query=\"...\" (names, words of an error, or what the code does in a few words).", overview);
        Assert.True(overview.Length <= ProjectOverview.MaxChars);
    }

    [Fact]
    public async Task The_projects_notes_for_agents_come_whole_and_past_their_size_its_start_and_end()
    {
        var project = Path.Combine(_dir, "notes");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var rules = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"- Rule {i}: keep the code of part {i} in its own folder."));
        var agents = rules + "\n\n## Notes\n- The tests need the emulator: run `pnpm emu` first.\n";
        Write("notes/AGENTS.md", agents);
        Write("notes/CLAUDE.md", agents);
        for (var i = 1; i <= 3; i++)
            Write($"notes/.cursor/rules/rule{i}.mdc", $"Cursor rule {i}.\n");
        Write("notes/src/app.ts", "export class App {\n  run(): void {}\n}\n");

        var overview = (await Run(Ctx(), project)).Text;
        // Whole, the note at its end too, and ahead of other tools' files, which no longer crowd it out.
        Assert.Contains("Notes for AI agents in the project:\n(AGENTS.md, 63 lines, all of it)\n" + agents + "(CLAUDE.md: the same as AGENTS.md)\n", overview);
        Assert.Contains("Cursor rule 1.", overview);
        // Outside the overview's own size: the rest of it is all there.
        Assert.Contains("Main files and what they define:", overview);
        Assert.True(overview.Length - agents.Length <= ProjectOverview.MaxChars);

        var lines = Enumerable.Range(1, 3000).Select(i => $"- Note {i}: something learned about part {i}.");
        Write("notes/AGENTS.md", string.Join("\n", lines) + "\n");
        File.Delete(Path.Combine(project, "CLAUDE.md"));
        overview = (await Run(Ctx(), project)).Text;
        Assert.Contains("(AGENTS.md, 3,000 lines, too long to give whole: its start and its end)\n- Note 1: something", overview);
        Assert.Contains("- Note 3000: something learned about part 3000.", overview);
        var skipped = System.Text.RegularExpressions.Regex.Match(overview, @"… lines (\d+)-(\d+) left out: read_file path=""(.+?)"" offset=(\d+) limit=(\d+) reads them\n");
        Assert.True(skipped.Success);
        var (first, last) = (int.Parse(skipped.Groups[1].Value), int.Parse(skipped.Groups[2].Value));
        Assert.Equal(Path.Combine(project, "AGENTS.md"), skipped.Groups[3].Value);
        Assert.Equal(first, int.Parse(skipped.Groups[4].Value));
        Assert.Equal(last - first + 1, int.Parse(skipped.Groups[5].Value));
        Assert.Contains($"- Note {first - 1}: ", overview);
        Assert.DoesNotContain($"- Note {first}: ", overview);
        Assert.DoesNotContain($"- Note {last}: ", overview);
        Assert.Contains($"- Note {last + 1}: ", overview);
        Assert.True(overview.Length <= ProjectOverview.MaxChars + ProjectOverview.MaxNotesChars + 200);
    }

    [Fact]
    public async Task A_library_kept_in_the_project_does_not_fill_its_map()
    {
        // A library's files use one another's names over and over, so each ranks high: in a Unity
        // game the map was half the Spine runtime. Each part gets lines by the code in it first.
        var project = Path.Combine(_dir, "game");
        Write("game/Assets/Game/GameState.cs", "public class GameState { }\n");
        for (var i = 0; i < 600; i++)
            Write($"game/Assets/Game/PlayerPart{i:000}.cs", $"public class PlayerPart{i:000}\n{{\n    public void Tick(GameState state) {{ }}\n}}\n");
        var vendor = string.Join(" ", Enumerable.Range(0, 12).Select(i => $"VendorNode{i:00} n{i};"));
        for (var i = 0; i < 12; i++)
            Write($"game/Assets/Vendor/Node{i:00}.cs", $"public class VendorNode{i:00}\n{{\n    {vendor}\n}}\n");

        var overview = (await Run(Ctx(), project)).Text;
        var map = overview[overview.IndexOf("Main files and what they define:")..];
        // Its share of the room: 12 of 613 files. Ranked alone, all twelve came before every PlayerPart.
        Assert.True(map.Split('\n').Count(l => l.Contains("VendorNode")) <= 4, map);
        Assert.True(map.Split('\n').Count(l => l.Contains("PlayerPart")) >= 10, map);
        Assert.Contains("GameState.cs", map);
    }

    [Fact]
    public async Task Documents_are_searched_by_their_text_with_read_files_line_numbers()
    {
        var folder = Path.Combine(_dir, "reports");
        Directory.CreateDirectory(folder);
        var docx = Path.Combine(folder, "q3.docx");
        await new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = docx, ["content"] = "# Q3 report\n\nSales grew in Norway.\n\nThe warehouse in Oslo closed in August." }, Ctx(), CancellationToken.None);
        Write("reports/notes.txt", "Nothing about it here.\n");

        var found = (await Run(Ctx(), folder, "when did the Oslo warehouse close")).Text;
        Assert.Contains($"1. {docx}, lines", found);
        var line = int.Parse(System.Text.RegularExpressions.Regex.Match(found, @"\n   (\d+): The warehouse in Oslo").Groups[1].Value);
        var read = (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = docx, ["offset"] = line, ["limit"] = 1 }, Ctx(), CancellationToken.None)).Text;
        Assert.StartsWith($"{line}\tThe warehouse in Oslo closed in August.", read);
    }

    [Fact]
    public async Task A_project_the_model_goes_into_without_indexing_gets_one_note()
    {
        var project = Path.Combine(_dir, "big");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        for (var i = 0; i < ProjectNudge.MinFiles; i++)
            Write($"big/src/File{i}.cs", "class C {}");
        var file = Path.Combine(project, "src", "File3.cs");
        var ctx = Ctx();
        var read = new ReadFileTool();
        var ok = new ToolResult("1\tclass C {}");

        var nudge = new ProjectNudge();
        Assert.Equal(ProjectNudge.Note(project, "a git repository"), nudge.Check(read, Args(file), ok, ctx));
        Assert.Null(nudge.Check(read, Args(file), ok, ctx)); // once per project
        nudge.Forget(); // a context reset took the overview away
        Assert.NotNull(nudge.Check(new ListDirectoryTool(), Args(Path.Combine(project, "src")), ok, ctx));

        // Indexed in this chat: nothing to say; a command naming the folder counts too.
        var indexed = new ProjectNudge();
        Assert.Null(indexed.Check(new IndexFolderTool(), Args(project), new ToolResult("Indexed"), ctx));
        Assert.Null(indexed.Check(read, Args(file), ok, ctx));
        Assert.NotNull(new ProjectNudge().Check(new ShellTool(), new JsonObject { ["command"] = $"Get-ChildItem \"{project}\\src\" -Recurse" }.ToJsonString(), ok, ctx));
        // An argument under another name ("file_path") is read as the tool reads it.
        Assert.NotNull(new ProjectNudge().Check(read, new JsonObject { ["file_path"] = file }.ToJsonString(), ok, ctx));

        // Sloperator's own Data folder is no project of the user's, even inside a checkout of Sloperator.
        var own = Path.Combine(_dir, "bbrepo");
        Directory.CreateDirectory(Path.Combine(own, ".git"));
        for (var i = 0; i < ProjectNudge.MinFiles; i++)
            Write($"bbrepo/src/File{i}.cs", "class C {}");
        var scratch = Path.Combine(own, "publish", "Data", "scratch");
        var download = Write("bbrepo/publish/Data/scratch/pgtest/pg.jar", "x");
        var bbCtx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir, IndexDirectory = _store, ScratchDirectory = scratch };
        Assert.Null(new ProjectNudge().Check(new ShellTool(), new JsonObject { ["command"] = $"cd \"{Path.GetDirectoryName(download)}\"; Expand-Archive pg.zip" }.ToJsonString(), ok, bbCtx));
        Assert.NotNull(new ProjectNudge().Check(read, Args(Path.Combine(own, "src", "File1.cs")), ok, bbCtx));

        // A small project is read faster than indexed; a file in no project is no matter.
        Directory.CreateDirectory(Path.Combine(_dir, "small", ".git"));
        Write("small/main.py", "print(1)");
        Assert.Null(new ProjectNudge().Check(read, Args(Path.Combine(_dir, "small", "main.py")), ok, ctx));
        Write("loose/notes.txt", "x");
        Assert.Null(new ProjectNudge().Check(read, Args(Path.Combine(_dir, "loose", "notes.txt")), ok, ctx));
        // Nor is a file on another computer.
        Assert.Null(new ProjectNudge().Check(read, new JsonObject { ["path"] = file, ["host"] = "me@server" }.ToJsonString(), ok, ctx));

        static string Args(string path) => new JsonObject { ["path"] = path }.ToJsonString();
    }

    [Fact]
    public async Task A_projects_notes_for_agents_come_with_the_first_call_in_it_once()
    {
        var read = new ReadFileTool();
        var ok = new ToolResult("1\tprint(1)");
        var held = new List<string>();
        var ctx = Ctx();
        ctx.InConversation = text => held.Any(h => h.Contains(text, StringComparison.Ordinal));

        // Too small to index: the notes alone.
        Directory.CreateDirectory(Path.Combine(_dir, "small", ".git"));
        Write("small/main.py", "print(1)");
        Write("small/AGENTS.md", "# Agents\n\n## Notes\n- Run it with python -X utf8.\n");
        var notes = ProjectOverview.ProjectNotes(Path.Combine(_dir, "small"));
        Assert.Equal("(AGENTS.md, 4 lines, all of it)\n# Agents\n\n## Notes\n- Run it with python -X utf8.\n", notes);
        var small = new ProjectNudge();
        var note = small.Check(read, Args(Path.Combine(_dir, "small", "main.py")), ok, ctx);
        Assert.Equal($"[Sloperator] {Path.Combine(_dir, "small")} is a project (a git repository) with {ProjectNudge.NotesLead}\n{notes.TrimEnd()}", note);
        Assert.True(ProjectNudge.GivesNotes(note!));
        Assert.False(ProjectNudge.AsksToIndex(note!));
        Assert.Null(small.Check(read, Args(Path.Combine(_dir, "small", "main.py")), ok, ctx)); // once per project
        // A new message's session doesn't give them again while the conversation holds them, unchanged.
        held.Add(note!);
        Assert.Null(new ProjectNudge().Check(read, Args(Path.Combine(_dir, "small", "main.py")), ok, ctx));
        Write("small/AGENTS.md", "# Agents\n\n## Notes\n- Run it with python -X utf8.\n- The tests need a .env.\n");
        Assert.Contains("- The tests need a .env.", new ProjectNudge().Check(read, Args(Path.Combine(_dir, "small", "main.py")), ok, ctx));
        // Without index_folder too; and a small project without notes gets nothing, as before.
        held.Clear();
        Assert.NotNull(new ProjectNudge().Check(read, Args(Path.Combine(_dir, "small", "main.py")), ok, ctx, canIndex: false));
        Directory.CreateDirectory(Path.Combine(_dir, "bare", ".git"));
        Write("bare/main.py", "print(1)");
        Assert.Null(new ProjectNudge().Check(read, Args(Path.Combine(_dir, "bare", "main.py")), ok, ctx));

        // Big enough to index: one note says to index it and gives the notes, which the overview then doesn't repeat.
        var project = Path.Combine(_dir, "big");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        for (var i = 0; i < ProjectNudge.MinFiles; i++)
            Write($"big/src/File{i}.cs", "class C {}");
        Write("big/CLAUDE.md", "Build with dotnet build -c Release.\n");
        note = new ProjectNudge().Check(read, Args(Path.Combine(project, "src", "File1.cs")), ok, ctx);
        Assert.StartsWith(ProjectNudge.Note(project, "a git repository") + "\nIts " + ProjectNudge.NotesLead + "\n(CLAUDE.md, 1 line, all of it)\nBuild with dotnet build -c Release.", note);
        Assert.True(ProjectNudge.AsksToIndex(note!) && ProjectNudge.GivesNotes(note!));
        held.Add(note!);
        var overview = (await Run(ctx, project)).Text;
        Assert.Contains("Notes for AI agents in the project:\n(CLAUDE.md: given whole in Sloperator's note on this project above, as it is now)", overview);
        Assert.DoesNotContain("dotnet build -c Release", overview);
        // Without index_folder, the notes alone.
        Assert.False(ProjectNudge.AsksToIndex(new ProjectNudge().Check(read, Args(Path.Combine(project, "src", "File1.cs")), ok, Ctx(), canIndex: false)!));

        static string Args(string path) => new JsonObject { ["path"] = path }.ToJsonString();
    }

    [Fact]
    public void The_project_a_path_is_in_is_found_by_what_it_has()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "repo", ".git"));
        Write("repo/src/App/App.csproj", "<Project />");
        Write("repo/src/App/Program.cs", "x");
        Assert.Equal((Path.Combine(_dir, "repo"), "a git repository"), ProjectRoot.Find(Path.Combine(_dir, "repo", "src", "App", "Program.cs")));

        Write("unity/Assets/Scripts/Hero.cs", "x");
        Directory.CreateDirectory(Path.Combine(_dir, "unity", "ProjectSettings"));
        Write("unity/Packages/com.me.tool/package.json", "{}");
        Assert.Equal((Path.Combine(_dir, "unity"), "a Unity project"), ProjectRoot.Find(Path.Combine(_dir, "unity", "Assets", "Scripts", "Hero.cs")));

        Write("site/package.json", "{}");
        Write("site/node_modules/left-pad/package.json", "{}");
        Assert.Equal((Path.Combine(_dir, "site"), "it has package.json"), ProjectRoot.Find(Path.Combine(_dir, "site", "node_modules", "left-pad", "package.json")));

        Write("plain/notes.txt", "x");
        Assert.Null(ProjectRoot.Find(Path.Combine(_dir, "plain", "notes.txt")));
    }

    [Fact]
    public async Task The_agent_is_told_to_index_a_project_it_reads_without_indexing()
    {
        var project = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        for (var i = 0; i < ProjectNudge.MinFiles; i++)
            Write($"proj/f{i}.txt", "hello");
        string Json(string name) => new JsonObject { ["path"] = Path.Combine(project, name) }.ToJsonString();
        var server = new ScriptedServer(
            Sse.ToolCall("c1", "read_file", Json("f1.txt")),
            Sse.ToolCall("c2", "read_file", Json("f2.txt")),
            Sse.Text("It says hello."));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };
        var session = new AgentSession(new Llm.OpenAiChatClient(new HttpClient(server), "http://test", null), new Llm.ChatRequestOptions(),
            ToolRegistry.CreateDefault(includeScreenTools: false), ctx, "system prompt");

        var observer = new RecordingObserver();
        await session.RunAsync("What does f1.txt say?", observer, CancellationToken.None);

        // Said after the result, in a message of its own.
        var c1 = session.History.FindIndex(m => m.ToolCallId == "c1");
        Assert.Equal(ProjectNudge.Note(project, "a git repository"), session.History[c1 + 1].Content);
        Assert.Single(session.History, m => m.Content?.StartsWith("[Sloperator]") == true);
        Assert.DoesNotContain(session.History, m => m.Role == Llm.ChatRole.Tool && m.Content!.Contains("[Sloperator]"));
        Assert.Contains($"Asked the model to index {project} before going through its files.", observer.Notices);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_agent_gets_a_small_projects_notes_once_across_messages_with_or_without_the_index(bool index)
    {
        var project = Path.Combine(_dir, "tiny");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Write("tiny/main.py", "print('hello')");
        Write("tiny/AGENTS.md", "## Notes\n- Run it with python -X utf8.\n");
        var read = new JsonObject { ["path"] = Path.Combine(project, "main.py") }.ToJsonString();
        var server = new ScriptedServer(
            Sse.ToolCall("c1", "read_file", read),
            Sse.ToolCall("c2", "read_file", read),
            Sse.Text("It prints hello."),
            Sse.ToolCall("c3", "read_file", read),
            Sse.Text("Still hello."));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir, IndexDirectory = _store };
        var session = new AgentSession(new Llm.OpenAiChatClient(new HttpClient(server), "http://test", null), new Llm.ChatRequestOptions(),
            ToolRegistry.CreateDefault(includeScreenTools: false, includeIndex: index), ctx, "system prompt");

        var observer = new RecordingObserver();
        await session.RunAsync("What does main.py print?", observer, CancellationToken.None);
        await session.RunAsync("And now?", observer, CancellationToken.None);

        // After the first call in the project only, in a message of its own, not again for the next message.
        var c1 = session.History.FindIndex(m => m.ToolCallId == "c1");
        Assert.Equal(ProjectNudge.Note(project, "a git repository", ProjectOverview.ProjectNotes(project), index: false), session.History[c1 + 1].Content);
        Assert.Contains("- Run it with python -X utf8.", session.History[c1 + 1].Content);
        Assert.Single(session.History, m => m.Content?.StartsWith("[Sloperator]") == true);
        Assert.Equal([$"Gave the model the notes for AI agents in {project}."], observer.Notices.Where(n => n.Contains(project)));
    }

    [Fact]
    public void The_prompt_asks_for_notes_in_the_projects_AGENTS_md_with_or_without_the_index()
    {
        foreach (var index in new[] { true, false })
        {
            var prompt = SystemPrompt.Build("Test OS", null, "C:\\", "", folderIndex: index);
            Assert.Contains("A project's AGENTS.md (or its CLAUDE.md, when it has only that) in its top folder holds notes for AI agents", prompt);
            Assert.Contains("under a \"## Notes\" heading; make AGENTS.md if the project has neither", prompt);
            Assert.Contains("never change the user's lines, and never write secrets there", prompt);
            Assert.Contains("Say in your reply what you noted.", prompt);
        }
    }

    [Fact]
    public async Task A_search_that_finds_nothing_says_what_the_index_leaves_out()
    {
        var project = Path.Combine(_dir, "web");
        Write("web/package.json", "{}");
        Write("web/src/app.js", "function startApp() {}");
        Write("web/node_modules/lib/stripes.js", "function zebraStripes() {}");

        var found = (await Run(Ctx(), project, "zebraStripes")).Text;

        Assert.Contains("No file in", found);
        Assert.Contains("The index doesn't search what it leaves out (node_modules", found);
    }

    [Fact]
    public void An_index_call_that_made_no_index_is_not_taken_for_one_in_the_next_message()
    {
        var project = Path.Combine(_dir, "stopped");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        for (var i = 0; i < ProjectNudge.MinFiles; i++)
            Write($"stopped/f{i}.txt", "hello");
        var ctx = Ctx();
        // Stopped while the index was being built: the call is in the chat, no index is.
        var history = new List<Llm.ChatMessage>
        {
            new() { Role = Llm.ChatRole.Assistant, ToolCalls = [new Llm.ToolCall { Id = "c1", Name = "index_folder", Arguments = new JsonObject { ["path"] = project }.ToJsonString() }] },
            Llm.ChatMessage.ToolResult("c1", "Stopped."),
        };
        var nudge = new ProjectNudge();
        nudge.Recall(history, ctx);

        Assert.NotNull(nudge.Check(new ReadFileTool(), new JsonObject { ["path"] = Path.Combine(project, "f1.txt") }.ToJsonString(), new ToolResult("1\thello"), ctx));
    }

    [Fact]
    public async Task The_next_message_of_a_chat_is_not_told_again_what_this_one_was()
    {
        // The app starts a session for every message, with the chat so far.
        var told = Path.Combine(_dir, "told");
        var indexed = Path.Combine(_dir, "indexed");
        foreach (var project in new[] { told, indexed })
        {
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            for (var i = 0; i < ProjectNudge.MinFiles; i++)
                Write($"{Path.GetFileName(project)}/f{i}.txt", "hello");
        }
        string Json(string project, string name) => new JsonObject { ["path"] = Path.Combine(project, name) }.ToJsonString();
        var ctx = Ctx();
        var history = new List<Llm.ChatMessage>();
        AgentSession Session(params string[] replies) => new(new Llm.OpenAiChatClient(new HttpClient(new ScriptedServer(replies)), "http://test", null),
            new Llm.ChatRequestOptions(), ToolRegistry.CreateDefault(includeScreenTools: false), ctx, "system prompt", history);

        await Session(
            Sse.ToolCall("c1", "read_file", Json(told, "f1.txt")),
            Sse.ToolCall("c2", "index_folder", new JsonObject { ["path"] = indexed }.ToJsonString()),
            Sse.Text("Read and indexed.")).RunAsync("Read f1 and index the other", new RecordingObserver(), CancellationToken.None);
        Assert.Equal(ProjectNudge.Note(told, "a git repository"), history[history.FindIndex(m => m.ToolCallId == "c1") + 1].Content);

        await Session(
            Sse.ToolCall("c3", "read_file", Json(told, "f2.txt")),
            Sse.ToolCall("c4", "read_file", Json(indexed, "f2.txt")),
            Sse.Text("Both say hello.")).RunAsync("And f2 in both?", new RecordingObserver(), CancellationToken.None);
        Assert.Single(history, m => m.Content?.StartsWith("[Sloperator]") == true);
    }

    [Fact]
    public void The_tool_is_offered_and_the_prompt_says_to_index_first()
    {
        var registry = ToolRegistry.CreateDefault(includeScreenTools: false);
        Assert.IsType<IndexFolderTool>(registry.Find("index_folder"));
        Assert.Equal(ToolRisk.ReadOnly, registry.Find("index_folder")!.Risk);
        Assert.IsType<IndexFolderTool>(ToolCallRepair.ResolveTool("codebase_search", registry));
        Assert.Contains("index it with index_folder", SystemPrompt.Build("Test OS", null, "C:\\", ""));
        Assert.Equal(Path.Combine("C:\\Data", "index"), new AppPaths("C:\\").IndexDir);
        // Left out to measure what it changes (sloperator-cli --no-index): the tool and the prompt's line.
        Assert.Null(ToolRegistry.CreateDefault(includeScreenTools: false, includeIndex: false).Find("index_folder"));
        Assert.DoesNotContain("index_folder", SystemPrompt.Build("Test OS", null, "C:\\", "", folderIndex: false));
    }

    [Fact]
    public async Task Questions_about_this_repository_find_the_file_that_answers_them()
    {
        var (index, _) = await FolderIndex.OpenAsync(Repository(), null, null, TestContext.Current.CancellationToken);
        var ranks = RepositoryQuestions.Select(q => FileRank(index, q.Query, "src", q.File)).ToList();
        // Measured when it was written: see docs/notes.md, Folder index.
        Assert.True(ranks.Count(r => r == 1) >= RepositoryQuestions.Length * 2 / 3, "first: " + string.Join(" ", ranks));
        Assert.True(ranks.Count(r => r is >= 1 and <= 3) >= RepositoryQuestions.Length * 9 / 10, "in the first three: " + string.Join(" ", ranks));
    }
}
