using System.Text;
using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Chats;
using Sloperator.Core.Diffs;
using Sloperator.Core.Projects;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// What the Code editor panel reads and writes: a folder's entries for its tree, a file opened to edit
/// and saved as it was written (encoding, signature, line breaks), the folder a chat had open, and the
/// project the agent worked in.
/// </summary>
public class CodeEditorFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-code-" + Guid.NewGuid().ToString("N")[..8]);

    public CodeEditorFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(string relative, string text = "")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_folder_lists_its_folders_first_in_explorers_order_without_what_vs_code_hides()
    {
        Make("file10.txt");
        Make("File2.txt");
        Make(".gitignore");
        Make("src/a.cs");
        Make("assets/b.png");
        Make(".git/HEAD");
        Make("Thumbs.db");

        var names = FolderListing.List(_root).Select(e => (e.Name, e.IsFolder)).ToList();

        Assert.Equal([("assets", true), ("src", true), (".gitignore", false), ("File2.txt", false), ("file10.txt", false)], names);
        Assert.Empty(FolderListing.List(Path.Combine(_root, "not there")));
    }

    [Fact]
    public async Task A_file_saved_keeps_its_signature_and_line_breaks_and_the_rest_of_its_bytes()
    {
        var path = Path.Combine(_root, "notes.cs");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("// café\r\nclass A { }\r\n")).ToArray();
        File.WriteAllBytes(path, bytes);

        var (file, why) = await CodeFile.ReadAsync(path, CancellationToken.None);
        Assert.Null(why);
        Assert.Equal("// café\r\nclass A { }\r\n", file!.Text);
        await file.SaveAsync(file.Text.Replace("class A", "class B"), CancellationToken.None);

        var saved = File.ReadAllBytes(path);
        Assert.Equal(bytes.Length, saved.Length);
        Assert.Equal(Encoding.UTF8.GetPreamble(), saved[..3]);
        Assert.Equal("// café\r\nclass B { }\r\n", Encoding.UTF8.GetString(saved, 3, saved.Length - 3));
    }

    [Fact]
    public async Task A_file_in_the_ansi_code_page_is_saved_in_it_again()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding(1252);
        var path = Path.Combine(_root, "old.ini");
        File.WriteAllBytes(path, ansi.GetBytes("name=Zoë\n"));

        var (file, _) = await CodeFile.ReadAsync(path, CancellationToken.None);
        Assert.Equal("name=Zoë\n", file!.Text);
        await file.SaveAsync("name=Zoë Müller\n", CancellationToken.None);

        Assert.Equal(ansi.GetBytes("name=Zoë Müller\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_powershell_script_typed_past_ascii_gets_utf8s_signature()
    {
        var path = Make("build.ps1", "Write-Host done\n");
        var (file, _) = await CodeFile.ReadAsync(path, CancellationToken.None);

        await file!.SaveAsync("Write-Host done ✓\n", CancellationToken.None);

        Assert.Equal(Encoding.UTF8.GetPreamble(), File.ReadAllBytes(path)[..3]);
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "CRLF", false)]
    [InlineData("a\nb\nc", "LF", false)]
    [InlineData("a\nb\r\nc\n", "LF", true)]
    [InlineData("one line", "CRLF", false)]
    public void A_files_line_breaks_are_the_ones_most_of_its_lines_end_in(string text, string name, bool mixed)
    {
        var (lineBreak, isMixed) = CodeFile.LineBreakOf(text);
        Assert.Equal((name, mixed), (CodeFile.NameOfBreak(lineBreak), isMixed));
    }

    [Theory]
    [InlineData(@"C:\game\src\Ship.cs", "C#")]
    [InlineData(@"C:\game\Water.shader", "C++")]
    [InlineData(@"C:\game\player.gd", null)]
    [InlineData(@"C:\game\Dockerfile", null)]
    public void A_file_is_highlighted_as_its_extension_says(string path, string? language) =>
        Assert.Equal(language, CodeLanguages.Of(path));

    [Fact]
    public void A_language_picked_for_a_kind_of_file_holds_for_every_file_of_it()
    {
        // Kept by extension, or by the name of a file with none, as settings.json keeps them.
        var picked = new Dictionary<string, string> { [CodeLanguages.Key(@"C:\game\player.GD")] = "Python", ["dockerfile"] = "PowerShell", [".cs"] = CodeLanguages.PlainText, [".java"] = "Brainfuck" };

        Assert.Equal("Python", CodeLanguages.For(@"D:\other\enemy.gd", picked));
        Assert.Equal("PowerShell", CodeLanguages.For(@"C:\game\Dockerfile", picked));
        Assert.Null(CodeLanguages.For(@"C:\game\src\Ship.cs", picked));
        // A name no longer offered: as the extension says.
        Assert.Equal("Java", CodeLanguages.For(@"C:\game\Main.java", picked));
        Assert.Equal("XML", CodeLanguages.For(@"C:\game\Game.csproj", picked));
    }

    [Fact]
    public void Other_line_breaks_picked_end_every_line_in_them() =>
        Assert.Equal("a\nb\nc\nd", CodeFile.WithLineBreaks("a\r\nb\nc\rd", "\n"));

    [Fact]
    public async Task Another_encoding_picked_is_written_on_save_and_every_encoding_read_is_offered()
    {
        var path = Make("notes.txt", "Zoë\n");
        var (file, _) = await CodeFile.ReadAsync(path, CancellationToken.None);
        Assert.Contains(CodeFile.Encodings, e => CodeFile.Same(e, file!.Encoding));
        Assert.Equal("UTF-8", CodeFile.NameOf(file!.Encoding));

        file.Encoding = CodeFile.Encodings.First(e => CodeFile.NameOf(e) == "UTF-16 LE");
        Assert.True(file.EncodingChanged);
        await file.SaveAsync(file.Text, CancellationToken.None);

        Assert.False(file.EncodingChanged);
        Assert.Equal([0xFF, 0xFE, (byte)'Z', 0], File.ReadAllBytes(path)[..4]);
        Assert.Equal(CodeFile.Encodings.Count, CodeFile.Encodings.Select(CodeFile.NameOf).Distinct().Count());
    }

    [Fact]
    public void An_encoding_that_cant_write_a_character_says_which_and_where()
    {
        var ansi = CodeFile.Encodings[^1];
        Assert.Equal(('✓', 2), CodeFile.Unwritable("plain\ndone ✓", ansi));
        Assert.Null(CodeFile.Unwritable("plain\ndone ✓", CodeFile.Encodings[0]));
    }

    [Fact]
    public async Task A_binary_or_huge_file_is_not_opened_and_says_why()
    {
        var picture = Path.Combine(_root, "a.png");
        File.WriteAllBytes(picture, [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0x0D]);
        var huge = Path.Combine(_root, "big.log");
        using (var stream = File.Create(huge))
            stream.SetLength(CodeFile.MaxBytes + 1);

        Assert.Equal("Not a text file.", (await CodeFile.ReadAsync(picture, CancellationToken.None)).Why);
        Assert.StartsWith("Too big to edit here", (await CodeFile.ReadAsync(huge, CancellationToken.None)).Why);
        Assert.Equal("The file isn't there any more.", (await CodeFile.ReadAsync(Path.Combine(_root, "gone.txt"), CancellationToken.None)).Why);
    }

    [Fact]
    public async Task Read_again_it_takes_the_disks_text_and_encoding()
    {
        var path = Make("a.txt", "one\n");
        var (file, _) = await CodeFile.ReadAsync(path, CancellationToken.None);
        File.WriteAllText(path, "two ✓\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("two ✓\n", await file!.ReadAgainAsync(CancellationToken.None));
        await file.SaveAsync("three ✓\n", CancellationToken.None);
        Assert.Equal(Encoding.UTF8.GetPreamble(), File.ReadAllBytes(path)[..3]);
    }

    [Fact]
    public void The_folder_open_in_the_code_editor_is_kept_with_the_chat()
    {
        var store = new ChatStore(Path.Combine(_root, "chats"));
        var chat = new SavedChat { Id = ChatStore.NewId(), Title = "Fix the build", Updated = DateTime.UtcNow, Folder = @"C:\Projects\game" };
        store.Save(chat);

        Assert.Equal(@"C:\Projects\game", store.Load(chat.Id)!.Folder);
    }

    [Fact]
    public async Task The_project_the_agent_worked_in_is_the_git_repository_of_the_first_file_it_changed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", ".git"));
        var first = Make("game/src/Ship.cs", "class Ship { }\n");
        var other = Make("elsewhere/notes.txt", "a\n");
        var changes = new FileChanges();
        Assert.Null(changes.Project());
        Assert.Null(changes.LastChange);

        await Written(changes, (first, "class Ship { int Lives; }\n"), (other, "b\n"));

        Assert.Equal(Path.Combine(_root, "game"), changes.Project());
        // Watch goes to the file of the last call, numbered so a new call tells from one followed already.
        Assert.Equal((Path.GetFullPath(other), 2), changes.LastChange);
    }

    [Fact]
    public async Task What_the_agent_makes_in_documents_opens_at_its_own_folder_there()
    {
        // Documents\Sloperator\<task>, where what the agent makes goes when the user names no place.
        var made = Path.Combine(_root, "Documents", "Sloperator");
        var snake = new FileChanges();
        await Written(snake, (Path.Combine(made, "Snake", "src", "game.js"), "let score = 0;\n"));
        Assert.Equal(Path.Combine(made, "Snake"), snake.Project(made));

        // A project inside the task's folder is still the project.
        Make(@"Documents\Sloperator\Shop\server\package.json", "{}");
        var shop = new FileChanges();
        await Written(shop, (Path.Combine(made, "Shop", "server", "src", "app.js"), "listen();\n"));
        Assert.Equal(Path.Combine(made, "Shop", "server"), shop.Project(made));

        // A file right in Documents\Sloperator: that folder.
        var note = new FileChanges();
        await Written(note, (Path.Combine(made, "hello.txt"), "hi\n"));
        Assert.Equal(made, note.Project(made));
    }

    /// <summary>Files written as the agent writes them: the file read before the call, the change noted after it.</summary>
    private async Task Written(FileChanges changes, params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            var args = new JsonObject { ["path"] = path, ["content"] = text };
            var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _root, Changes = changes };
            var call = changes.Starting(new WriteFileTool(), args, ctx);
            await new WriteFileTool().ExecuteAsync(args, ctx, CancellationToken.None);
            await call!.EndAsync();
        }
    }
}
