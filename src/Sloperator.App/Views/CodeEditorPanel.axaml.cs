using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>The Code editor panel, on the left of the chat window: a folder's files in a tree, and the file open in the editor beside it.</summary>
public partial class CodeEditorPanel : UserControl
{
    private CodeEditorViewModel? _vm;

    /// <summary>What is selected goes into the box once the selection rests a moment: a drag or Shift+arrows would redo the box's card at every step.</summary>
    private readonly DispatcherTimer _pickTimer;

    /// <summary>Set while the editor lets go of its selection because a message went with it.</summary>
    private bool _lettingGo;

    public CodeEditorPanel()
    {
        InitializeComponent();
        _pickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _pickTimer!.Stop();
            PickSelection();
        });
        var area = Editor.TextArea;
        area.SelectionChanged += (_, _) =>
        {
            if (_lettingGo)
                return;
            _pickTimer.Stop();
            _pickTimer.Start();
        };
        // Selected text is what goes with a message: the yellow tint of what is picked anywhere, under the
        // code's own colours. The line with the caret a card's shade, the caret the text's colour.
        area.SelectionBrush = Brush("BbAccentTint");
        area.SelectionBorder = null;
        area.SelectionForeground = null;
        area.SelectionCornerRadius = 0;
        area.CaretBrush = Brush("BbText");
        area.TextView.CurrentLineBackground = Brush("BbSurface");
        // Its border a pen that draws nothing: null left AvaloniaEdit's own green line round it.
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        // A link in code isn't followed by a click: the click is for the caret.
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.IndentationSize = 4;
        // The numbers 8 in from the panel's edge (LineNumbersMargin, set in markup, reached a margin made
        // before it), and no dotted line between them and the code: room instead, as the diff's gutter has.
        foreach (var numbers in area.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>())
            numbers.Margin = new Thickness(8, 0, 0, 0);
        foreach (var line in area.LeftMargins.OfType<Line>())
        {
            line.Stroke = Brushes.Transparent;
            line.Margin = new Thickness(4, 0, 4, 0);
        }
        // The encodings and line breaks offered, the file's own ticked: one picked converts the whole file.
        EncodingButton.Entries = () => Core.Projects.CodeFile.Encodings.Select(encoding =>
            Pick(Core.Projects.CodeFile.NameOf(encoding), _vm?.IsEncoding(encoding) == true, () => _vm?.PickEncoding(encoding)));
        LineBreakButton.Entries = () => Core.Projects.CodeFile.LineBreaks.Select(lineBreak =>
            Pick(lineBreak.Name, _vm?.IsLineBreak(lineBreak.Break) == true, () => _vm?.PickLineBreak(lineBreak.Break)));
        Tree.AddHandler(TappedEvent, OnTreeTapped);
        Tree.AddHandler(KeyDownEvent, OnTreeKey, RoutingStrategies.Tunnel);
    }

    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    /// <summary>An entry of the encoding or line-break menu: a radio item, ticked for the file's.</summary>
    private static Control Pick(string name, bool current, Action pick)
    {
        var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = current };
        item.Click += (_, _) => pick();
        return item;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.LetGoOfSelection -= LetGoOfSelection;
            _vm.Lit -= Editor.Light;
        }
        _vm = DataContext as CodeEditorViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.LetGoOfSelection += LetGoOfSelection;
            _vm.Lit += Editor.Light;
        }
        ShowDocument();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeEditorViewModel.Document))
            ShowDocument();
    }

    /// <summary>The file open in the editor, highlighted by its kind, indented with tabs where it is.</summary>
    private void ShowDocument()
    {
        var document = _vm?.Document;
        Editor.SyntaxHighlighting = _vm?.OpenPath is { } path ? CodeView.For(path) : null;
        Editor.Document = document ?? new TextDocument();
        Editor.Options.ConvertTabsToSpaces = document is null || !IndentsWithTabs(document);
    }

    /// <summary>A line among the first thousand starts with a tab.</summary>
    private static bool IndentsWithTabs(TextDocument document) =>
        document.Lines.Take(1000).Any(l => l.Length > 0 && document.GetCharAt(l.Offset) == '\t');

    // ---- the selection, into the box ------------------------------------------------------------

    /// <summary>
    /// The selection as lines: the first and the last it is in (one that ends where a line starts, as
    /// whole lines picked by Shift+Down do, leaves that line out), the text itself, and whether it is
    /// the whole file. Nothing selected takes them out of the box.
    /// </summary>
    private void PickSelection()
    {
        if (_vm is null || _vm.Document is not { } document || Editor.Document != document)
            return;
        var selection = Editor.TextArea.Selection;
        if (selection.IsEmpty)
        {
            _vm.Unpick();
            return;
        }
        var segment = selection.SurroundingSegment;
        var from = document.GetLineByOffset(segment.Offset).LineNumber;
        var end = document.GetLineByOffset(segment.EndOffset);
        var to = end.LineNumber > from && end.Offset == segment.EndOffset ? end.LineNumber - 1 : end.LineNumber;
        _vm.Selected(from, to, selection.GetText(), segment.Offset == 0 && segment.EndOffset == document.TextLength);
    }

    /// <summary>The message went with the selected lines: the editor lets go of them, the caret staying where it is.</summary>
    private void LetGoOfSelection()
    {
        _lettingGo = true;
        try { Editor.TextArea.ClearSelection(); }
        finally { _lettingGo = false; }
        _pickTimer.Stop();
    }

    /// <summary>Ctrl+S saves; Esc lets go of the selected lines, as a click away does.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is null)
            return;
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            if (_vm.SaveCommand.CanExecute(null))
                _vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !Editor.TextArea.Selection.IsEmpty)
        {
            Editor.TextArea.ClearSelection();
            e.Handled = true;
        }
    }

    // ---- the tree -------------------------------------------------------------------------------

    private static FileTreeRow? RowOf(object? source) =>
        (source as Control)?.FindLogicalAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FileTreeRow;

    /// <summary>A click on a folder unfolds or folds it, on a file opens it.</summary>
    private void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        if (RowOf(e.Source) is { } row)
            Activate(row);
    }

    private void Activate(FileTreeRow row)
    {
        if (_vm is null)
            return;
        if (row.IsFolder)
            _vm.Toggle(row);
        else
            _ = _vm.OpenAsync(row.Path);
    }

    /// <summary>Enter opens the file or folds the folder; Right unfolds, then goes into it; Left folds, then goes up to the folder it is in.</summary>
    private void OnTreeKey(object? sender, KeyEventArgs e)
    {
        if (_vm?.SelectedRow is not { } row || e.KeyModifiers != KeyModifiers.None)
            return;
        switch (e.Key)
        {
            case Key.Enter:
                Activate(row);
                break;
            case Key.Right when row.IsFolder && !row.IsExpanded:
                _vm.Expand(row, true);
                break;
            case Key.Right when row.IsFolder && _vm.Tree.Rows.IndexOf(row) is var at && at + 1 < _vm.Tree.Rows.Count && _vm.Tree.Rows[at + 1].Depth > row.Depth:
                _vm.SelectedRow = _vm.Tree.Rows[at + 1];
                break;
            case Key.Left when row is { IsFolder: true, IsExpanded: true }:
                _vm.Expand(row, false);
                break;
            case Key.Left when _vm.Tree.Parent(row) is { } parent:
                _vm.SelectedRow = parent;
                break;
            default:
                return;
        }
        if (_vm.SelectedRow is { } selected)
            Tree.ScrollIntoView(selected);
        e.Handled = true;
    }

    /// <summary>
    /// A right-click on a file: every file's menu (Open in Inspector, Copy file or Copy image, see
    /// <see cref="FileMenu"/>) with Show in folder before Copy, as File changes' list has; on a folder, Show in folder.
    /// </summary>
    private void OnTreeMenu(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.FindLogicalAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: FileTreeRow row } item)
            return;
        e.Handled = true;
        var show = new MenuItem { Header = "Show in folder" };
        show.Click += (_, _) => FilePreview.ShowInFolder(row.Path);
        var menu = new ContextMenu();
        foreach (var entry in row.IsFolder ? [show] : (List<Control>)[.. FileMenu.Open(item, row.Path), show, .. FileMenu.Copy(item, null, null, row.Path)])
            menu.Items.Add(entry);
        menu.Open(item);
    }

    /// <summary>Open folder: a folder picked from the PC is the chat's folder from now on.</summary>
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var start = _vm.Folder is { } folder ? await storage.TryGetFolderFromPathAsync(folder) : null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a folder in the Code editor",
            SuggestedStartLocation = start,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            _vm.OpenFolder(path);
    }
}
