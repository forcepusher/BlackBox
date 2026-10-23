using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Projects;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

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

    /// <summary>The BlackBox repository these tests run in.</summary>
    private static string Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles("BlackBox.slnx").Any())
                return dir.FullName;
        throw new InvalidOperationException("Not run from the repository.");
    }

    /// <summary>Questions about BlackBox's own code and the file that answers each, as the model might ask them.</summary>
    internal static readonly (string Query, string File)[] RepositoryQuestions =
    [
        ("where the settings are saved to disk", "AppSettings.cs"),
        ("how a click is aimed with a fresh look at the screen", "FreshLook.cs"),
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
        Assert.Contains("index (BlackBox's indexes)", again);
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
            Write($"shared/src/Part{i}.cs", $"public class Part{i}\n{{\n    public int Weight => {i};\n}}\n");
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
        Assert.Contains("Notes for AI agents in the project:\n(AGENTS.md, 1 line; the start of it)\nUse pnpm, not npm.", overview);
        Assert.Contains("TypeScript 2 files", overview);
        Assert.Contains($"src{Path.DirectorySeparatorChar} 2", overview);
        Assert.Contains($"src{Path.DirectorySeparatorChar}cart.ts (5 lines): class Cart (addItem); functions checkout", overview);
        Assert.Contains($"src{Path.DirectorySeparatorChar}app.tsx (2 lines): App", overview);
        Assert.Contains("Listed but not read: 1 pictures", overview);
        Assert.EndsWith($"To find anything in it: index_folder path=\"{project}\" query=\"...\" (names, words of an error, or what the code does in a few words).", overview);
        Assert.True(overview.Length <= ProjectOverview.MaxChars);
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

        await session.RunAsync("What does f1.txt say?", new RecordingObserver(), CancellationToken.None);

        var results = session.History.Where(m => m.Role == Llm.ChatRole.Tool).Select(m => m.Content!).ToList();
        Assert.EndsWith(ProjectNudge.Note(project, "a git repository"), results[0]);
        Assert.DoesNotContain("[BlackBox]", results[1]);
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
        Assert.Contains(ProjectNudge.Note(told, "a git repository"), history.Single(m => m.ToolCallId == "c1").Content);

        await Session(
            Sse.ToolCall("c3", "read_file", Json(told, "f2.txt")),
            Sse.ToolCall("c4", "read_file", Json(indexed, "f2.txt")),
            Sse.Text("Both say hello.")).RunAsync("And f2 in both?", new RecordingObserver(), CancellationToken.None);
        Assert.DoesNotContain("[BlackBox]", history.Single(m => m.ToolCallId == "c3").Content);
        Assert.DoesNotContain("[BlackBox]", history.Single(m => m.ToolCallId == "c4").Content);
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
