using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Media;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

// Markdown files the chat previews formatted: which files, how much of one is read, and write_file showing one it wrote.
public class MarkdownFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-md-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    [Theory]
    [InlineData("README.md", true)]
    [InlineData(@"C:\notes\Plan.MARKDOWN", true)]
    [InlineData("notes.txt", false)]
    [InlineData("report.docx", false)]
    public void Markdown_is_known_by_its_extension(string path, bool handled) => Assert.Equal(handled, MarkdownFiles.Handles(path));

    [Fact]
    public void A_short_file_is_read_whole_in_its_own_encoding()
    {
        var file = Path.Combine(_dir, "short.md");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(file, "# Café\n\n- 5 €\n", Encoding.Unicode);
        var (text, cut) = MarkdownFiles.ReadStart(file);
        Assert.Equal("# Café\n\n- 5 €\n", text);
        Assert.False(cut);
    }

    [Fact]
    public void A_long_file_is_cut_at_a_whole_line()
    {
        var file = Path.Combine(_dir, "long.md");
        File.WriteAllText(file, string.Concat(Enumerable.Range(1, 100).Select(i => $"Line number {i:000}\n")));
        var (text, cut) = MarkdownFiles.ReadStart(file, max: 50);
        Assert.True(cut);
        Assert.Equal("Line number 001\nLine number 002\nLine number 003", text);
    }

    [Fact]
    public async Task Writing_a_Markdown_file_shows_it_under_the_call_and_a_text_file_does_not()
    {
        var md = Path.Combine(_dir, "plan.md");
        var txt = Path.Combine(_dir, "plan.txt");
        var write = new WriteFileTool();
        var made = await write.ExecuteAsync(new JsonObject { ["path"] = md, ["content"] = "# Plan" }, Ctx(), CancellationToken.None);
        var added = await write.ExecuteAsync(new JsonObject { ["path"] = md, ["content"] = "\n- more", ["append"] = true }, Ctx(), CancellationToken.None);
        var plain = await write.ExecuteAsync(new JsonObject { ["path"] = txt, ["content"] = "plan" }, Ctx(), CancellationToken.None);
        Assert.Equal([md], made.Files!);
        Assert.Equal([md], added.Files!);
        Assert.Null(plain.Files);
    }
}
