using System.Text.RegularExpressions;
using BlackBox.Core.Agent;
using BlackBox.Core.Chats;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// What a toolkit panel gives a message shows as cards like its files', over the box and in the sent
/// bubble, and a tool's card never has the × a file of the user's has (the user, 2026-10-25).
/// </summary>
public class ToolCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-cards-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_messages_cards_read_back_and_one_saved_before_them_has_none()
    {
        var store = new ChatStore(_dir);
        var clapper = "M18 4l2 4h-3z";
        store.Save(new SavedChat
        {
            Id = "cards",
            Title = "Cards",
            Entries =
            [
                new ChatEntry { Kind = ChatEntryKind.User, Text = "what is this", Via = "File changes · notes.md", Cards = [new ToolCard("Lines 3-5 · notes.md", Path: @"C:\w\notes.md")] },
                new ChatEntry { Kind = ChatEntryKind.User, Text = "make it", Via = "Video generator · Fox", Cards = [new ToolCard("Fox", "whole video", Icon: clapper)] },
            ],
        });
        File.WriteAllText(Path.Combine(_dir, "old.json"), """{"id":"old","title":"Old","entries":[{"kind":"User","text":"Hi","via":"Video generator · Fox"}]}""");

        var entries = store.Load("cards")!.Entries;
        Assert.Equal(new ToolCard("Lines 3-5 · notes.md", Path: @"C:\w\notes.md"), Assert.Single(entries[0].Cards!));
        Assert.Equal(new ToolCard("Fox", "whole video", Icon: clapper), Assert.Single(entries[1].Cards!));
        Assert.Null(Assert.Single(store.Load("old")!.Entries).Cards);
    }

    /// <summary>
    /// Every × on a card in the box shows only for what can be taken off (AttachmentItem.CanRemove,
    /// false for a tool's pick), and the card of what a message is about in the tool has none: a panel
    /// added later puts its picks in the box through the same card, and so gets no × either.
    /// </summary>
    [Fact]
    public void No_tool_card_in_the_box_has_an_x()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "src", "BlackBox.App", "Views", "MainWindow.axaml")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("The repository was not found above the tests.");
        var window = File.ReadAllText(Path.Combine(root, "src", "BlackBox.App", "Views", "MainWindow.axaml"));
        var buttons = Regex.Matches(window, @"<Button\s[^>]*Classes=""removeAttachment""[^>]*>");
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Contains(@"IsVisible=""{Binding CanRemove}""", b.Value));

        var attachments = File.ReadAllText(Path.Combine(root, "src", "BlackBox.App", "ViewModels", "Attachments.cs"));
        Assert.Contains("public bool CanRemove => _remove is not null && !Picked;", attachments);
        var card = attachments[attachments.IndexOf("class ToolCardItem", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Remove", card);
    }
}
