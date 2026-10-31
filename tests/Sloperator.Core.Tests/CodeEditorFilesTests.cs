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

        // As the agent writes them: the file read before the call, the change noted after it.
        foreach (var (path, text) in new[] { (first, "class Ship { int Lives; }\n"), (other, "b\n") })
        {
            var args = new JsonObject { ["path"] = path, ["content"] = text };
            var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _root, Changes = changes };
            var call = changes.Starting(new WriteFileTool(), args, ctx);
            await new WriteFileTool().ExecuteAsync(args, ctx, CancellationToken.None);
            await call!.EndAsync();
        }

        Assert.Equal(Path.Combine(_root, "game"), changes.Project());
        // Watch goes to the file of the last call, numbered so a new call tells from one followed already.
        Assert.Equal((Path.GetFullPath(other), 2), changes.LastChange);
    }
}
