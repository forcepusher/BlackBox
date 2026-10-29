using Sloperator.Core.Agent;
using Sloperator.Core.Chats;
using Sloperator.Core.Diffs;
using Sloperator.Core.Llm;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Undo message and everything below: where the model's conversation is cut, and what the chat says after.</summary>
public class ChatCutTests
{
    private static ChatMessage From(UserInput input) => input.ToMessage(seesImages: false);
    private static ChatMessage Reply(string text) => new() { Role = ChatRole.Assistant, Content = text, Reasoning = "thinking about " + text };

    [Fact]
    public void The_conversation_is_cut_where_the_model_had_the_message()
    {
        UserInput a = new("Make a file"), b = new("Now rename it"), c = new("Thanks");
        List<ChatMessage> history = [From(a), Reply("made"), ChatMessage.User("[A note of Sloperator's]"), From(b), Reply("renamed"), From(c), Reply("bye")];

        Assert.Equal(0, ChatCut.HistoryStart(history, [a, b, c], 0));
        Assert.Equal(3, ChatCut.HistoryStart(history, [a, b, c], 1));
        Assert.Equal(5, ChatCut.HistoryStart(history, [a, b, c], 2));
    }

    [Fact]
    public void Messages_sent_during_a_run_sent_together_or_said_again_are_found_by_their_words()
    {
        UserInput task = new("Draw a cat"), during = new("make it orange"), late1 = new("ok"), late2 = new("Continue."), again = new("Continue.");
        // One sent while the model worked comes marked; two sent as the run ended go as one; Continue twice.
        var marked = ChatMessage.User("[Sent while you were working on \"Draw a cat\". Answer it …]\nmake it orange");
        marked.FromUser = true;
        List<ChatMessage> history =
        [
            From(task), Reply("drawing"), marked, Reply("orange now"),
            From(UserInput.Join([late1, late2])), Reply("going on"), From(again), Reply("done"),
        ];
        UserInput[] sent = [task, during, late1, late2, again];

        Assert.Equal(2, ChatCut.HistoryStart(history, sent, 1));
        Assert.Equal(4, ChatCut.HistoryStart(history, sent, 2));
        // The second of two sent together: their message goes (the one before it shares it).
        Assert.Equal(4, ChatCut.HistoryStart(history, sent, 3));
        // The same words again are the later message.
        Assert.Equal(6, ChatCut.HistoryStart(history, sent, 4));
    }

    [Fact]
    public void A_message_the_model_never_had_cuts_where_the_next_begins_or_nowhere()
    {
        UserInput a = new("first"), lost = new("never arrived"), b = new("second");
        List<ChatMessage> history = [From(a), Reply("1"), From(b), Reply("2")];

        Assert.Equal(2, ChatCut.HistoryStart(history, [a, lost, b], 1));
        Assert.Equal(4, ChatCut.HistoryStart(history, [a, b, lost], 2));
    }

    [Fact]
    public void Files_alone_and_a_message_with_context_are_found()
    {
        var file = Path.Combine(Path.GetTempPath(), "bb-cut-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        UserInput hi = new("hi"), files = new("", [file]), video = new("Make it.") { Context = "[In the Video generator: clip 2]" };
        List<ChatMessage> history = [From(hi), Reply("hello"), From(files), Reply("read"), From(video), Reply("made")];

        Assert.Equal(2, ChatCut.HistoryStart(history, [hi, files, video], 1));
        Assert.Equal(4, ChatCut.HistoryStart(history, [hi, files, video], 2));
    }

    [Fact]
    public void After_a_context_reset_the_user_messages_kept_are_where_it_cuts()
    {
        UserInput a = new("Build the game"), b = new("Add sound");
        // A reset keeps the user's messages and the handoff note after them.
        List<ChatMessage> history = [From(a), From(b), ChatMessage.User("[Context reset] note"), Reply("sound added")];

        Assert.Equal(1, ChatCut.HistoryStart(history, [a, b], 1));
    }

    [Fact]
    public void A_cut_before_a_reset_keeps_its_handoff_note()
    {
        UserInput a = new("Build the game"), b = new("Add sound"), c = new("Make it louder");
        var note = ChatMessage.User(AgentSession.HandoffHeader + "\n\nBuilt the game in C:\\game; sound half done.");
        List<ChatMessage> history = [From(a), From(b), note, Reply("sound added"), From(c), Reply("louder")];

        // Undoing "Add sound": the note stays, said to tell of work undone since.
        ChatCut.Cut(history, ChatCut.HistoryStart(history, [a, b, c], 1));
        Assert.Equal(3, history.Count);
        Assert.Equal("Build the game", history[0].Content);
        Assert.Same(note, history[1]);
        Assert.Equal(ChatCut.UndoneNote, history[2].Content);
        Assert.False(history[2].FromUser);

        // After the reset, a cut is as it was: the note is before it.
        List<ChatMessage> later = [From(a), From(b), note, Reply("sound added"), From(c), Reply("louder")];
        ChatCut.Cut(later, ChatCut.HistoryStart(later, [a, b, c], 2));
        Assert.Equal(4, later.Count);
        Assert.Same(note, later[2]);
    }

    [Fact]
    public void The_chat_says_what_was_put_back_and_what_wasnt()
    {
        var back = new FileChanges.RollBack([@"C:\p\notes.txt"], [@"C:\p\made.txt"], [(@"C:\p\report.docx", "It is open in Word.")]);
        var said = ChatCut.Said(back, [("write_file", true), ("run_shell", true), ("run_shell", true), ("screenshot", true), ("copy", false), ("mouse_click", true)]);

        Assert.Equal("Put back as before your message: notes.txt. Moved to the Recycle Bin, as made after it: made.txt. " +
                     "Couldn't put back report.docx: It is open in Word. " +
                     "Not undone, as Sloperator doesn't keep what they change: run_shell ×2 and mouse_click.", said);
        // Only looking and replying: nothing to say.
        Assert.Null(ChatCut.Said(new FileChanges.RollBack([], [], []), [("screenshot", true), ("read_file", true)]));
        // A message from before messages had turns: its files can't be put back.
        Assert.StartsWith("The files write_file and edit_file changed weren't put back", ChatCut.Said(null, [("edit_file", true)]));
    }

    [Fact]
    public void Pictures_the_conversation_no_longer_names_are_deleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bb-cut-chats-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new ChatStore(dir);
            var folder = store.PicturesOf("chat");
            Directory.CreateDirectory(folder);
            foreach (var name in new[] { "0001.jpg", "0002.png", "0003.jpg", "notes.txt" })
                File.WriteAllText(Path.Combine(folder, name), "x");

            store.DropPicturesBut("chat", new HashSet<string> { "0001.jpg" });
            Assert.Equal(["0001.jpg", "notes.txt"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order());

            File.Delete(Path.Combine(folder, "notes.txt"));
            store.DropPicturesBut("chat", new HashSet<string>());
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
