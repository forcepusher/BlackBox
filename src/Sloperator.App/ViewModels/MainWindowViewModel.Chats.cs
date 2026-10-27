using System.Collections.ObjectModel;
using Sloperator.Core.Agent;
using Sloperator.Core.Chats;
using Sloperator.Core.Diffs;
using Sloperator.Core.Llm;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- saved chats (the History dropdown) --------------------------------------------

    /// <summary>Saved chats, the most recently changed first.</summary>
    public ObservableCollection<PastChat> PastChats { get; } = [];

    public bool HasPastChats => PastChats.Count > 0;

    /// <summary>The file the chat on screen is saved in; null until its first message.</summary>
    private string? _chatId;
    private string? _chatTitle;

    /// <summary>
    /// The chat on screen, by its file; null until its first message. Open in one window at a time:
    /// two each saving their own run into one file would lose one of them.
    /// </summary>
    internal string? ChatId => _chatId;

    /// <summary>The window's title: the chat's, so several Sloperator windows can be told apart on the taskbar.</summary>
    public string WindowTitle => _chatTitle is { } title ? $"{title} - Sloperator" : "Sloperator";

    /// <summary>
    /// The run in progress belongs to a chat that is no longer on screen (New chat or another chat
    /// was picked while it ran): it is being stopped, and what it still reports is dropped.
    /// </summary>
    private bool _runDetached;

    private PastChat NewRow(ChatHeader header) => new(header, OpenChat, DeleteChat);

    /// <summary>Clears the screen for another chat. A run in progress is stopped, and saved as it is unless the chat is being deleted.</summary>
    private void LeaveChat(bool save)
    {
        // Left before the model read them (or before they could start a run): back in the box, with
        // their files, to send again or not, before the chat left is saved.
        if (Queued.Count > 0)
        {
            List<UserInput> back = [], read = [];
            foreach (var input in Queued.Select(q => q.Input).ToList())
            {
                // One the agent has just taken is in its conversation: it joins the chat left, as
                // read. Put back in the box as well, it was in the saved chat unseen, and carried out
                // on Continue.
                if (_session is { } running && !_steerPending.Contains(input) && !_forNextRun.Contains(input) && !running.Unsteer(input))
                    read.Add(input);
                else
                    back.Add(input);
            }
            foreach (var input in read)
                MoveIntoChat(input);
            BackInTheBox(back);
            Queued.Clear();
        }
        if (IsBusy)
        {
            // Not when the run is one already ending for a chat left before: this one never ran.
            if (save && !_runDetached)
            {
                // Said in the chat, as when its window closes: opened again from History, it had
                // stopped without a word, and before the model had the message, "Continue." had no task.
                AddStopNotice("Stopped: another chat was opened.", isError: false, _unread);
                SaveChat(SnapshotHistory());
            }
            _runDetached = true;
            _runCts?.Cancel();
        }
        else if (save && _lastSavePending)
            SaveChat(SnapshotHistory()); // the last save of a run that has just ended, not made yet
        // The stopped run keeps its own list until it ends, so it can't write into the next chat.
        _history = [];
        _steerPending.Clear();
        _forNextRun.Clear();
        // Logins belong to the chat: the next one starts logged out (the stopped run's tools fail).
        // Logging out talks to the servers, so it happens off the UI thread.
        var ended = _ssh;
        _ = Task.Run(ended.Dispose);
        _ssh = new Core.Ssh.SshHosts(_paths.KnownHostsFile);
        foreach (var item in Items)
            item.DisposePreviews();
        Items.Clear();
        _current = null;
        _chatId = null;
        _chatTitle = null;
        UseChanges(new FileChanges());
        OnPropertyChanged(nameof(WindowTitle));
        ContextUsage = "";
    }

    private void OpenChat(PastChat row)
    {
        if (row.Id == _chatId)
            return;
        // Open in another window: that one comes forward, with the chat as it goes on there.
        var other = _windows.WithChat(row.Id, this);
        row.IsElsewhere = other is not null;
        if (other is not null)
        {
            _windows.BringForward(other);
            return;
        }
        // A chat left a moment ago, here or in another window, may still be on its way to the disk.
        _chatSaver.Flush(TimeSpan.FromSeconds(10));
        if (_chats.Load(row.Id) is not { } chat)
        {
            PastChats.Remove(row);
            OnPropertyChanged(nameof(HasPastChats));
            return;
        }
        LeaveChat(save: true);
        _chatId = chat.Id;
        _chatTitle = chat.Title;
        OnPropertyChanged(nameof(WindowTitle));
        _history = chat.History;
        UseChanges(FileChanges.Load(ChangesFile(chat.Id)));
        var withFiles = new List<(ChatItemViewModel, Func<IReadOnlyList<string>>)>();
        foreach (var entry in chat.Entries)
        {
            var item = FromEntry(entry);
            Items.Add(item);
            // The files come back with the chat, as long as they are still there.
            if (entry.Kind == ChatEntryKind.Tool && entry.Files is { Count: > 0 } files)
            {
                // Kept for a save before they are looked up, which would otherwise drop them.
                ((ToolCallItem)item).SavedFiles = files;
                withFiles.Add((item, () => files.Where(Core.Media.FileMentions.Exists).ToList()));
            }
            else if (item is AssistantMessageItem { HasText: true, Text: var text })
                withFiles.Add((item, () => Core.Media.FileMentions.Find(text)));
        }
        ShowFilesLater(withFiles);
        // A chat that ended stopped can be carried on from where it stopped; an earlier stop it went on from can't.
        foreach (var notice in Items.OfType<NoticeItem>())
            notice.CanContinue = notice.Stopped && ReferenceEquals(notice, Items[^1]);
        MarkCurrentChat();
    }

    private void DeleteChat(PastChat row)
    {
        // Another window's run would only write it back.
        if (_windows.WithChat(row.Id, this) is not null)
        {
            row.IsElsewhere = true;
            return;
        }
        // Its last version on the disk, for its pictures; and none still on its way there afterwards.
        _chatSaver.Flush(TimeSpan.FromSeconds(10));
        // The pictures pasted into it go with it; files dropped on the window were the user's own and stay.
        var pasted = (_chats.Load(row.Id)?.Entries ?? []).Where(e => e.Kind == ChatEntryKind.User).SelectMany(e => e.Files ?? [])
            .Where(f => string.Equals(Path.GetDirectoryName(f), _paths.PastedDir, StringComparison.OrdinalIgnoreCase)).ToList();
        if (row.Id == _chatId)
            LeaveChat(save: false);
        _chatSaver.Forget(row.Id);
        _chats.Delete(row.Id);
        try { File.Delete(ChangesFile(row.Id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        PastChats.Remove(row);
        OnPropertyChanged(nameof(HasPastChats));
        foreach (var file in pasted.Where(f => !Attachments.Any(a => string.Equals(a.Path, f, StringComparison.OrdinalIgnoreCase))))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void MarkCurrentChat()
    {
        foreach (var row in PastChats)
            row.IsCurrent = row.Id == _chatId;
    }

    /// <summary>
    /// The History list as the chats folder has it now, when it opens: other windows add chats to it
    /// as they go, and those they have open are marked.
    /// </summary>
    public void RefreshPastChats()
    {
        var headers = _chats.List();
        var rows = headers.Select(h =>
        {
            if (PastChats.FirstOrDefault(r => r.Id == h.Id) is not { } row)
                return NewRow(h);
            // The chat on screen may be newer here than on the disk.
            if (row.Id != _chatId) row.Updated = h.Updated;
            return row;
        }).ToList();
        // On its way to the disk for the first time.
        if (_chatId is not null && rows.All(r => r.Id != _chatId) && PastChats.FirstOrDefault(r => r.Id == _chatId) is { } current)
            rows.Insert(0, current);
        rows = rows.OrderByDescending(r => r.Updated).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var at = PastChats.IndexOf(rows[i]);
            if (at < 0) PastChats.Insert(i, rows[i]);
            else if (at != i) PastChats.Move(at, i);
            rows[i].IsElsewhere = _windows.WithChat(rows[i].Id, this) is not null;
        }
        while (PastChats.Count > rows.Count)
            PastChats.RemoveAt(PastChats.Count - 1);
        OnPropertyChanged(nameof(HasPastChats));
        MarkCurrentChat();
    }

    /// <summary>A copy of the model's conversation to save; null if a run changed it while it was being copied.</summary>
    private List<ChatMessage>? SnapshotHistory()
    {
        try { return ChatStore.ForSaving(_history); }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException) { return null; }
    }

    /// <summary>Writes the chat on screen to its file and moves it to the top of the History list. A chat with nothing typed yet isn't saved.</summary>
    private void SaveChat(List<ChatMessage>? history)
    {
        // A chat with nothing typed yet isn't saved. One whose first message the chat has dropped
        // to stay small (TrimChat, some 200 steps into a long run) still is: it stopped being
        // saved there, and the rest of the run was lost to History and to a crash.
        var first = Items.OfType<UserMessageItem>().FirstOrDefault();
        if (history is null || first is null && _chatTitle is null)
            return;
        _chatId ??= ChatStore.NewId();
        Changes.KeepIn(ChangesFile(_chatId));
        if (_chatTitle is null)
        {
            // A message that is only a file is named after it; one sent with Video generator open after
            // the video and piece too: History filled with chats named "Make it.".
            var text = first!.HasText ? first.Text : string.Join(", ", first.Attachments.Select(a => a.FileName));
            // "Make it. (Fox Test · start of clip 1)", without "Video generator" before the video's name.
            const string video = "Video generator · ";
            _chatTitle = ChatStore.TitleFrom(first.Via is { } via ? $"{text.Trim().Split('\n')[0]} ({(via.StartsWith(video, StringComparison.Ordinal) ? via[video.Length..] : via)})" : text);
            OnPropertyChanged(nameof(WindowTitle));
        }
        var chat = new SavedChat
        {
            Id = _chatId,
            Title = _chatTitle,
            Updated = DateTime.UtcNow,
            // A reply bubble with nothing in it yet (a step just started) is left out.
            Entries = Items.Where(i => i is not AssistantMessageItem { IsStreaming: true, HasText: false, HasReasoning: false }).Select(ToEntry).ToList(),
            History = history,
        };
        // Written in the background: a long run's chat is megabytes, at every step.
        _chatSaver.Save(chat);

        var row = PastChats.FirstOrDefault(r => r.Id == chat.Id);
        if (row is null)
        {
            row = NewRow(new ChatHeader { Id = chat.Id, Title = chat.Title, Updated = chat.Updated });
            PastChats.Insert(0, row);
            OnPropertyChanged(nameof(HasPastChats));
        }
        else
        {
            row.Updated = chat.Updated;
            if (PastChats.IndexOf(row) != 0)
                PastChats.Move(PastChats.IndexOf(row), 0);
        }
        MarkCurrentChat();
    }

    private static ChatEntry ToEntry(ChatItemViewModel item)
    {
        var entry = item switch
        {
            UserMessageItem u => new ChatEntry { Kind = ChatEntryKind.User, Text = u.Text, Files = u.Files.Count > 0 ? u.Files : null, Via = u.Via, Context = u.Context, Cards = u.Cards?.ToList() },
            AssistantMessageItem a => new ChatEntry
            {
                Kind = ChatEntryKind.Assistant, Text = a.Text, Reasoning = a.Reasoning, Ended = a.Ended, Sent = a.SentAt, FirstWords = a.FirstWords,
            },
            ToolCallItem t => new ChatEntry
            {
                Kind = ChatEntryKind.Tool, CallId = t.CallId, Name = t.Name, Summary = t.Summary, State = t.State.ToString(), Text = t.ResultText,
                Files = t.HasPreviews ? t.Previews.Select(p => p.Path).ToList() : t.SavedFiles?.ToList(), Ended = t.Ended,
            },
            NoticeItem n => new ChatEntry { Kind = ChatEntryKind.Notice, Text = n.Text, IsError = n.IsError, Stopped = n.Stopped, Unsent = n.Unsent },
            _ => throw new ArgumentOutOfRangeException(nameof(item)),
        };
        (entry.At, entry.RunTook) = (item.At, item.RunTook);
        return entry;
    }

    private ChatItemViewModel FromEntry(ChatEntry e)
    {
        ChatItemViewModel item = e.Kind switch
        {
            ChatEntryKind.User => new UserMessageItem(e.Text ?? "", e.Files, e.Cards) { Via = e.Via, Context = e.Context },
            ChatEntryKind.Assistant => new AssistantMessageItem
            {
                Text = e.Text ?? "", Reasoning = e.Reasoning ?? "", IsStreaming = false, Ended = e.Ended, SentAt = e.Sent, FirstWords = e.FirstWords,
            },
            ChatEntryKind.Tool => new ToolCallItem(e.CallId ?? "", e.Name ?? "", e.Summary ?? "")
            {
                ResultText = e.Text ?? "",
                // A call that was still running or waiting when the chat was saved never finished.
                State = Enum.TryParse<ToolItemState>(e.State, out var state) && state is not (ToolItemState.Running or ToolItemState.AwaitingApproval)
                    ? state : ToolItemState.Stopped,
                Ended = e.Ended,
            },
            _ => NoticeFrom(e),
        };
        // Lines saved before the chat kept times show none.
        (item.At, item.RunTook) = (e.At, e.RunTook);
        return item;
    }

    /// <summary>
    /// A saved notice. One a stopped run left gets its Continue back; if the model never had the
    /// message before it, that is the message the chat shows last so far, which Continue sends again.
    /// </summary>
    private NoticeItem NoticeFrom(ChatEntry e)
    {
        if (!e.Stopped)
            return new NoticeItem(e.Text ?? "", e.IsError);
        if (e.Unsent && Items.OfType<UserMessageItem>().LastOrDefault() is { } message)
        {
            var again = message.Input;
            return new NoticeItem(e.Text ?? "", e.IsError, () => _ = RunAgainAsync(again), unsent: true);
        }
        return new NoticeItem(e.Text ?? "", e.IsError, CarryOn);
    }
}
