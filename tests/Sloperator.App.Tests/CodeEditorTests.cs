using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// The Code editor panel: its tree of a folder (unfolded and read again as the disk changes, its rows
/// kept so the list stays where it was), what the model is told about the lines selected, the agent's
/// edits shown without moving the caret, and the highlighting in the app's colours.
/// </summary>
public sealed class CodeEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-tree-" + Guid.NewGuid().ToString("N")[..8]);

    public CodeEditorTests()
    {
        foreach (var file in new[] { "src/Game.cs", "src/Ship.cs", "assets/levels.json", "README.md" })
        {
            var path = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static List<string> Shown(FileTree tree) => tree.Rows.Select(r => new string(' ', r.Depth * 2) + r.Name).ToList();

    [Fact]
    public void A_folder_unfolds_under_itself_and_folds_back()
    {
        var tree = new FileTree(_ => false);
        tree.Show(_root);
        Assert.Equal(["assets", "src", "README.md"], Shown(tree));

        var src = tree.Find(Path.Combine(_root, "src"))!;
        tree.Expand(src, true);
        Assert.Equal(["assets", "src", "  Game.cs", "  Ship.cs", "README.md"], Shown(tree));
        Assert.Equal(src, tree.Parent(tree.Rows[2]));
        Assert.Null(tree.Parent(src));

        tree.Expand(src, false);
        Assert.Equal(["assets", "src", "README.md"], Shown(tree));
    }

    [Fact]
    public void Read_again_it_keeps_its_rows_and_what_is_unfolded_and_adds_whats_new()
    {
        var tree = new FileTree(path => path.EndsWith("Ship.cs", StringComparison.Ordinal));
        tree.Show(_root);
        var src = tree.Find(Path.Combine(_root, "src"))!;
        tree.Expand(src, true);
        var game = tree.Find(Path.Combine(_root, "src", "Game.cs"));
        Assert.True(tree.Find(Path.Combine(_root, "src", "Ship.cs"))!.IsUnsaved);

        File.WriteAllText(Path.Combine(_root, "src", "Bullet.cs"), "");
        File.Delete(Path.Combine(_root, "README.md"));
        Directory.CreateDirectory(Path.Combine(_root, "tools"));
        tree.Refresh([Path.Combine(_root, "src"), _root]);

        Assert.Equal(["assets", "src", "  Bullet.cs", "  Game.cs", "  Ship.cs", "tools"], Shown(tree));
        // The same rows: the list keeps its place and its selection.
        Assert.Same(src, tree.Find(Path.Combine(_root, "src")));
        Assert.Same(game, tree.Find(Path.Combine(_root, "src", "Game.cs")));
    }

    [Fact]
    public void A_folded_folder_read_before_is_read_again_too()
    {
        var tree = new FileTree(_ => false);
        tree.Show(_root);
        var src = tree.Find(Path.Combine(_root, "src"))!;
        tree.Expand(src, true);
        tree.Expand(src, false);

        File.WriteAllText(Path.Combine(_root, "src", "Rock.cs"), "");
        tree.Refresh(["*"]);
        tree.Expand(src, true);

        Assert.Equal(["assets", "src", "  Game.cs", "  Rock.cs", "  Ship.cs", "README.md"], Shown(tree));
    }

    [Fact]
    public void A_file_opened_from_its_menu_is_shown_with_its_folders_unfolded()
    {
        var tree = new FileTree(_ => false);
        tree.Show(_root);

        var ship = tree.Reveal(Path.Combine(_root, "src", "Ship.cs"));

        Assert.Equal("Ship.cs", ship?.Name);
        Assert.True(tree.Find(Path.Combine(_root, "src"))!.IsExpanded);
        Assert.Null(tree.Reveal(Path.Combine(Path.GetTempPath(), "elsewhere.cs")));
    }

    [Theory]
    [InlineData(12, 18, false, "Lines 12-18 · Game.cs")]
    [InlineData(4, 4, false, "Line 4 · Game.cs")]
    [InlineData(1, 40, true, "Whole file · Game.cs")]
    public void The_box_says_which_lines(int from, int to, bool whole, string caption) =>
        Assert.Equal(caption, CodeEditorViewModel.Caption((from, to, "", whole), "Game.cs"));

    [Fact]
    public void The_model_gets_the_lines_whole_and_numbered()
    {
        var (via, context) = CodeEditorViewModel.Told((31, 32, "for (var i = 0;\n        {", false), @"C:\game\src\Game.cs", unsaved: false,
            ["        for (var i = 0; i < 3; i++)", "        {"]);

        Assert.Equal("Code editor · Game.cs lines 31-32", via);
        Assert.Contains(@"with C:\game\src\Game.cs open. What they say is about what they selected in it unless they say otherwise, lines 31-32", context);
        Assert.Contains("31\t        for (var i = 0; i < 3; i++)\n32\t        {\n]", context);
        Assert.DoesNotContain("not saved", context);
    }

    [Fact]
    public void A_part_of_a_line_is_told_as_well_as_the_line_and_edits_not_saved_are_said()
    {
        var (_, context) = CodeEditorViewModel.Told((9, 9, "StartRocks", false), @"C:\game\src\Game.cs", unsaved: true, ["    private const int StartRocks = 4;"]);

        Assert.Contains("edited there and not saved yet", context);
        Assert.Contains("9\t    private const int StartRocks = 4;\nIn that line they selected: StartRocks\n]", context);
    }

    [Fact]
    public void A_long_selection_is_cut_with_a_count_of_the_rest()
    {
        var lines = Enumerable.Range(1, 5000).Select(i => $"line {i} of a long generated file").ToList();
        var (_, context) = CodeEditorViewModel.Told((1, 5000, "", true), @"C:\a.txt", unsaved: false, lines);

        Assert.True(context.Length < 21_000);
        Assert.Matches(@"\[\.\.\. \d+ more selected lines\]\n\]$", context);
    }

    [Fact]
    public void The_agents_edit_is_shown_with_the_caret_before_it_where_it_was()
    {
        var document = new TextDocument("one\ntwo\nthree\n");
        var caret = document.CreateAnchor(5);

        var (start, length) = CodeEditorViewModel.Replace(document, "one\ntwo\nTHREE\nfour\n");

        Assert.Equal("one\ntwo\nTHREE\nfour\n", document.Text);
        // What Watch lights: from the first character that differs to the last.
        Assert.Equal("THREE\nfour", document.GetText(start, length));
        Assert.Equal(5, caret.Offset);
        Assert.False(caret.IsDeleted);
    }

    [Theory]
    [InlineData("a\nb\n")]
    [InlineData("a\r\nb\r\n")]
    public void Enter_and_a_paste_keep_the_files_own_line_breaks(string text)
    {
        HeadlessApp.Run(() =>
        {
            var lineBreak = text.Contains('\r') ? "\r\n" : "\n";
            var editor = new CodeView { Document = new TextDocument(text) };
            new Window { Content = editor, Width = 400, Height = 300 }.Show();
            editor.CaretOffset = 1;
            editor.TextArea.PerformTextInput("\n");
            Assert.Equal($"a{lineBreak}{lineBreak}b{lineBreak}", editor.Text);

            editor.Document.Text = text;
            editor.CaretOffset = 1;
            var clipboard = TopLevel.GetTopLevel(editor)!.Clipboard!;
            clipboard.SetTextAsync("x\r\ny\nz").GetAwaiter().GetResult();
            editor.Paste();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal($"ax{lineBreak}y{lineBreak}z{lineBreak}b{lineBreak}", editor.Text);
            return true;
        });
    }

    [Fact]
    public void Ctrl_and_the_wheel_zoom_the_text_keeping_the_line_under_the_pointer()
    {
        HeadlessApp.Run(() =>
        {
            var editor = new CodeView { Document = new TextDocument(string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line {i}"))), FontSize = 12 };
            var window = new Window { Content = editor, Width = 600, Height = 400 };
            window.Show();
            editor.ScrollToLine(150);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var view = editor.TextArea.TextView;
            int LineAt(double y) => view.GetDocumentLineByVisualTop(view.VerticalOffset + y).LineNumber;
            var under = LineAt(200);
            Assert.True(under > 100, $"scrolled to {under}");

            window.MouseWheel(new Point(200, 200), new Vector(0, 1), RawInputModifiers.Control);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(13, editor.FontSize);
            Assert.Equal(under, LineAt(200));

            window.MouseWheel(new Point(200, 200), new Vector(0, -30), RawInputModifiers.Control);
            Assert.Equal(CodeEditorViewModel.MinFontSize, editor.FontSize);

            // Without Ctrl it scrolls, the size as it was.
            var offset = view.VerticalOffset;
            window.MouseWheel(new Point(200, 200), new Vector(0, -1), RawInputModifiers.None);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(CodeEditorViewModel.MinFontSize, editor.FontSize);
            Assert.True(view.VerticalOffset > offset);
            return true;
        });
    }

    [Fact]
    public void The_zoom_buttons_size_keeps_the_top_line_at_the_top()
    {
        HeadlessApp.Run(() =>
        {
            var editor = new CodeView { Document = new TextDocument(string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line {i}"))), FontSize = 12 };
            var window = new Window { Content = editor, Width = 600, Height = 400 };
            window.Show();
            var view = editor.TextArea.TextView;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            editor.ScrollToLine(150);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var top = view.GetDocumentLineByVisualTop(view.VerticalOffset).LineNumber;
            Assert.True(top > 100, $"scrolled to {top}");

            editor.FontSize = 16;
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(top, view.GetDocumentLineByVisualTop(view.VerticalOffset).LineNumber);
            return true;
        });
    }

    [Fact]
    public void The_lines_watch_lights_go_in_runs() =>
        Assert.Equal([(4, 6), (9, 9), (12, 13)], CodeEditorViewModel.Runs([13, 4, 5, 6, 9, 12, 5]));

    [Fact]
    public void Every_language_on_the_menu_has_highlighting_and_plain_text_none()
    {
        foreach (var language in Core.Projects.CodeLanguages.Names)
            Assert.True(CodeView.Definition(language) is not null, language);
        Assert.Null(CodeView.Definition(null));
    }

    [Theory]
    [InlineData("Game.cs", "C#")]
    [InlineData("App.tsx", "JavaScript")]
    [InlineData("MainWindow.axaml", "XML")]
    [InlineData("Water.hlsl", "C++")]
    [InlineData("README.md", "MarkDown")]
    [InlineData("build.ps1", "PowerShell")]
    [InlineData("notes.txt", null)]
    [InlineData("paper.tex", null)]
    public void A_file_is_highlighted_as_its_kind(string file, string? highlighting) =>
        Assert.Equal(highlighting, CodeView.Definition(Core.Projects.CodeLanguages.Of(file))?.Name);

    [Fact]
    public void Every_colour_avalonia_edit_names_is_one_of_the_apps_or_the_codes_own()
    {
        HeadlessApp.Run(() =>
        {
            var names = new List<string>();
            foreach (var definition in HighlightingManager.Instance.HighlightingDefinitions)
            {
                try { names.AddRange(definition.NamedHighlightingColors.Select(c => c.Name)); }
                catch (HighlightingDefinitionInvalidException) { } // TeX's doesn't load
            }
            Assert.NotEmpty(names);
            foreach (var key in names.Select(CodeView.SyntaxColour).OfType<string>().Distinct())
                Assert.True(Application.Current!.TryFindResource(key, out _), key);
            Assert.Equal("BbSyntaxComment", CodeView.SyntaxColour("Comment"));
            Assert.Equal("BbSyntaxString", CodeView.SyntaxColour("String"));
            Assert.Equal("BbSyntaxKeyword", CodeView.SyntaxColour("ValueTypeKeywords"));
            Assert.Equal("BbSyntaxMethod", CodeView.SyntaxColour("MethodCall"));
            Assert.Null(CodeView.SyntaxColour("StringInterpolation"));
            Assert.Null(CodeView.SyntaxColour("Punctuation"));
            return true;
        });
    }
}
