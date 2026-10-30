using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- IAgentObserver (called from the agent thread) -------------------------------

    // A run can last hours. The model's context is kept in check by context resets; the chat
    // view is kept in check here so memory stays flat: old entries are dropped and only recent
    // screenshots keep their thumbnails.
    private const int MaxChatItems = 400;
    private const int MaxThumbnails = 30;

    private void TrimChat()
    {
        while (Items.Count > MaxChatItems)
        {
            Items[0].DisposePreviews();
            Items.RemoveAt(0);
        }
        foreach (var old in Items.OfType<ToolCallItem>().Where(t => t.HasScreenshot).SkipLast(MaxThumbnails).ToList())
        {
            old.Screenshot?.Dispose();
            old.Screenshot = null;
            old.ScreenshotData = null;
        }
    }


    // Every callback below is dropped once the run is detached: the chat it belongs to is no longer on screen.

    /// <summary>The reply bubble the agent streams into; only touched on the agent's thread.</summary>
    private AssistantMessageItem? _streaming;

    /// <summary>
    /// Hands an update from the agent's thread to the window with the time it happened: the window
    /// can get to it a moment later, and the chat shows how long things took.
    /// </summary>
    private static void PostTimed(Action<DateTime> update)
    {
        var at = DateTime.UtcNow;
        Dispatcher.UIThread.Post(() => update(at));
    }

    void IAgentObserver.OnStepStarted(int step)
    {
        // The agent waits for this call, so its conversation holds still while it is copied and measured.
        var history = _runDetached ? null : SnapshotHistory();
        var usage = MeasureContext();
        var at = DateTime.UtcNow;
        var bubble = _streaming = new AssistantMessageItem { At = at };
        Dispatcher.UIThread.Post(() =>
        {
            if (_runDetached) return;
            // Saved at every step, so a crash or power cut loses at most the step in progress.
            SaveChat(history);
            TrimChat();
            FinishCurrent(at); // a bubble nothing streamed into (a request that failed) leaves no "…" behind
            _current = bubble;
            Items.Add(bubble);
            ShowContextUsage(usage);
        });
    }

    void IAgentObserver.OnUserMessageDelivered(UserInput input) => Dispatcher.UIThread.Post(() =>
    {
        if (_runDetached) return;
        MoveIntoChat(input);
    });

    void IAgentObserver.OnDelta(StreamDelta delta) => _streaming?.Append(delta);

    void IAgentObserver.OnSending(long sent, long total) => _streaming?.Sending(sent, total);

    void IAgentObserver.OnAssistantMessage(ChatMessage message)
    {
        // A reply carries the server's own token count, so the top bar is exact after each one.
        var usage = MeasureContext();
        PostTimed(at =>
        {
            if (_runDetached) return;
            FinishCurrent(at, message);
            ShowContextUsage(usage);
        });
    }

    /// <summary>How full the context is; on the agent's thread, while it waits and its conversation holds still.</summary>
    private (int Used, int Size, int Reset) MeasureContext() =>
        _session is { } session ? (session.TokensInUse(), session.ContextTokens, session.ResetThreshold) : default;

    private void ShowContextUsage((int Used, int Size, int Reset) usage)
    {
        if (usage.Size <= 0) return;
        ContextUsage = $"{usage.Used / 1000.0:0}k of {ContextSizes.Format(usage.Size)} used";
        ContextUsageTip = $"Starts a fresh context from a handoff note at {usage.Reset:N0} tokens.";
    }

    /// <summary>
    /// Ends the streaming bubble, whose step ended <paramref name="at"/>. An empty one (a discarded
    /// reply) leaves no trace; one that only called tools keeps its line with the step's time.
    /// </summary>
    private void FinishCurrent(DateTime at, ChatMessage? message = null)
    {
        if (_current is null) return;
        _current.Finish(at, message);
        if (!_current.HasText && !_current.HasReasoning && message is not { HasToolCalls: true })
            Items.Remove(_current);
        else if (message is not null && _current.HasText)
        {
            // The files the reply names (what the agent made) are shown under it, to look at, play or open.
            var text = _current.Text;
            ShowFilesLater([(_current, () => Core.Media.FileMentions.Find(text))]);
        }
        _current = null;
    }

    /// <summary>
    /// Shows under each line the files it names, looked for off the UI thread: a path on a server
    /// that is off (\\nas\…, a mapped drive whose server is gone) holds up the look for half a
    /// minute, and the window froze for as long when a reply or a reopened chat named one.
    /// </summary>
    private async void ShowFilesLater(List<(ChatItemViewModel Item, Func<IReadOnlyList<string>> Files)> lines)
    {
        try
        {
            var found = await Task.Run(() => lines.Select(l => (l.Item, Files: l.Files())).Where(l => l.Files.Count > 0).ToList());
            foreach (var (item, files) in found)
                if (Items.Contains(item))
                    item.AddPreviews(files, _previews);
        }
        catch (Exception ex)
        {
            CrashLog.Write("the files a chat names", ex);
        }
    }

    async Task<bool> IAgentObserver.RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_runDetached) return Task.FromResult(false);
            var item = FindOrAddTool(call, summary);
            var waiting = item.WaitForApprovalAsync(ct);
            NotifyUser(FirstLine($"Waiting for your OK: {summary}"));
            return waiting;
        });
    }

    /// <summary>The offer of text recognition on its card; one at a time while one is still waiting for an answer.</summary>
    void IAgentObserver.OfferTextRecognition() => PostTimed(at =>
    {
        if (_runDetached || _platform.Text is not { } reader || Items.OfType<TextRecognitionItem>().Any(i => i.IsOpen || i.IsInstalling))
            return;
        Items.Insert(NoticeIndex(), new TextRecognitionItem(reader, TurnOffTextRecognition) { At = at });
        NotifyUser("Text recognition isn't installed: install it?");
    });

    /// <summary>The card's Turn off: Settings' Use text recognition, from the next message on.</summary>
    private void TurnOffTextRecognition()
    {
        _settings.Agent.EnableTextRecognition = false;
        SaveSettings();
    }

    void IAgentObserver.OnNotice(string text) => ShowNotice(text, null);

    void IAgentObserver.OnNotice(string text, Exception error) => ShowNotice(text, error);

    /// <summary>A notice from the run; one about a server's failure (<paramref name="error"/>) goes into the error log too.</summary>
    private void ShowNotice(string text, Exception? error) => PostTimed(at =>
    {
        if (_runDetached) return;
        if (error is not null)
            Core.ErrorLog.Write(ChatPlace(), text, error);
        var notice = new NoticeItem(text, isError: false) { At = at };
        var waiting = WaitingReply();
        var index = NoticeIndex();
        // One line for a wait that goes on (a busy or unreachable server), not one per try: no
        // step starts meanwhile to trim the chat, and an hour of tries filled it.
        if (text.EndsWith(WaitNoticeEnd, StringComparison.Ordinal) && index > 0 && Items[index - 1] is NoticeItem { Text: var last } && last.EndsWith(WaitNoticeEnd, StringComparison.Ordinal))
            Items[index - 1] = notice;
        else
            Items.Insert(index, notice);
        // The reply that comes is the next try's, and is timed from it, as its sending is: under
        // the lines of the tries, it said it was asked before them.
        if (waiting is not null)
            waiting.At = at;
    });

    /// <summary>How AgentSession's notices of a wait before trying again end.</summary>
    private const string WaitNoticeEnd = "(Stop ends the wait)…";

    /// <summary>
    /// Where a notice goes in the chat: at the end, or before the reply still waiting for its first
    /// words. A request the server failed is sent again, and the reply that comes then fills that
    /// bubble: the lines about the tries went under it, and a task that had done its work ended
    /// on "Connection lost (The server broke off its answer…)", as if that had stopped it.
    /// </summary>
    private int NoticeIndex() => WaitingReply() is null ? Items.Count : Items.Count - 1;

    /// <summary>The step's reply at the end of the chat, nothing of it come yet; null if there is none.</summary>
    private AssistantMessageItem? WaitingReply() =>
        Items.Count > 0 && Items[^1] is AssistantMessageItem { IsStreaming: true, FirstWords: null } waiting && ReferenceEquals(waiting, _current)
            ? waiting
            : null;

    /// <summary>The chat, as the error log names it: its title and file, once it has them.</summary>
    private string ChatPlace() =>
        _chatId is null ? "a new chat" : $"chat \"{_chatTitle}\", Data\\chats\\{_chatId}.json";

    void IAgentObserver.OnToolStarted(ToolCall call, string summary) => PostTimed(at =>
    {
        if (_runDetached) return;
        var item = FindOrAddTool(call, summary);
        item.Summary = summary;
        // Its time counts from here: a wait for the user's OK before it is not the call's.
        item.At = at;
        item.State = ToolItemState.Running;
    });

    void IAgentObserver.OnToolProgress(ToolCall call, string text) => Dispatcher.UIThread.Post(() =>
    {
        if (_runDetached) return;
        if (Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == call.Id) is { State: ToolItemState.Running } item)
            item.Progress = text;
    });

    void IAgentObserver.OnToolFinished(ToolCall call, ToolResult result) => PostTimed(at =>
    {
        if (_runDetached) return;
        var item = FindOrAddTool(call, call.Name);
        item.ResultText = result.Text.Length > 3000 ? result.Text[..3000] + "\n…" : result.Text.TrimEnd();
        item.Progress = "";
        // Files the call made or showed get a preview (a player, a model to turn), which says more
        // than the picture made for the model; anything else keeps its screenshot.
        if (result.Files is { Count: > 0 } files)
        {
            // Looked for in the background, as a reply's: a ComfyUI save_to or a document on a share.
            item.SavedFiles = files;
            ShowFilesLater([(item, () => files.Where(Core.Media.FileMentions.Exists).ToList())]);
        }
        else if (result.Image is not null)
        {
            // An MCP server's picture can be one the window doesn't read (an SVG, a broken file):
            // the call's card goes without it rather than the window closing.
            try
            {
                using var ms = new MemoryStream(result.Image);
                item.Screenshot = Bitmap.DecodeToWidth(ms, 480);
                item.ScreenshotData = result.Image;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        if (item.State == ToolItemState.AwaitingApproval)
            item.State = ToolItemState.Stopped; // the run was stopped while the call waited for an OK; Deny would have said Declined
        else
        {
            item.Ended = at;
            if (item.State is not (ToolItemState.Denied or ToolItemState.Stopped))
                item.State = AgentSession.IsCutShort(result.Text) ? ToolItemState.Stopped
                    : result.IsError ? ToolItemState.Error : ToolItemState.Done;
        }
    });

    /// <summary>Set when the run is ending on a bug (see <see cref="IAgentObserver.OnFailed"/>), whose stack trace went into the crash log.</summary>
    private volatile bool _failureLogged;

    void IAgentObserver.OnFailed(Exception error)
    {
        CrashLog.Write("the agent", error);
        _failureLogged = true;
    }

    void IAgentObserver.OnFinished(AgentStopReason reason, string? detail) => Finished(reason, detail, null);

    void IAgentObserver.OnFinished(AgentStopReason reason, string? detail, Exception error) => Finished(reason, detail, error);

    private void Finished(AgentStopReason reason, string? detail, Exception? error)
    {
        // Read here: by the time the window gets to this, a run of what came too late for this one may have started.
        var started = _runStarted;
        var logged = _failureLogged;
        _failureLogged = false;
        var compacting = _compacting;
        PostTimed(at =>
        {
            var stopped = StoppedText();
            if (_runDetached) return;
            FinishCurrent(at);
            // A call cut short by the stop says so; it used to read "Declined", as if the user had said no to it.
            foreach (var t in Items.OfType<ToolCallItem>().Where(t => t.State is ToolItemState.Running or ToolItemState.AwaitingApproval))
            {
                if (t.State == ToolItemState.Running) t.Ended = at;
                t.State = ToolItemState.Stopped;
            }

            switch (reason)
            {
                case AgentStopReason.Cancelled:
                    AddStopNotice(stopped, isError: false, compacting: compacting);
                    break;
                case AgentStopReason.Error:
                    AddStopNotice((detail ?? "Something went wrong.") + (logged ? @" Details are in Data\crash.log." : ""), isError: true, error: error, compacting: compacting);
                    break;
            }

            // How long the run took, under the line it ended with: its last reply, or the notice it
            // stopped with. A message of the user's that came too late for it is the next run's.
            if (Items.LastOrDefault(i => i is not UserMessageItem && i.At >= started) is { } end)
                end.RunTook = at - started;

            // Not for a stop: the user stopped it. Nor for a task that goes on with what they sent
            // too late for this run, nor for a context compacted, which the user waits for.
            if (reason == AgentStopReason.Error)
                NotifyUser(FirstLine($"Stopped by an error: {detail ?? "Something went wrong."}"));
            else if (reason == AgentStopReason.Completed && !compacting && !HasQueued)
                NotifyUser(Items.OfType<AssistantMessageItem>().LastOrDefault(m => m.At >= started && m.HasText) is { } reply ? FirstLine(reply.ShownText) : "Done.");
        });
    }

    /// <summary>
    /// The run ended or waits for the user: said in a Windows notification, when they asked for one
    /// in Settings and aren't looking at this window (see <see cref="ChatWindows.Notify"/>).
    /// </summary>
    private void NotifyUser(string line)
    {
        if (_settings.Notify.WhenDone && !_closed)
            _windows.Notify(this, _chatTitle ?? "Sloperator", line);
    }

    /// <summary>
    /// A reply's first line, for a notification: its first paragraph without the Markdown marks,
    /// cut short past <paramref name="max"/> characters, about two lines of a notification.
    /// </summary>
    public static string FirstLine(string markdown, int max = 140)
    {
        // The first line of text, not of a code block.
        var (line, inCode) = ("", false);
        foreach (var text in markdown.Split('\n').Select(l => l.Trim()))
        {
            if (text.StartsWith("```", StringComparison.Ordinal))
                inCode = !inCode;
            else if (!inCode && text.Length > 0)
            {
                line = text;
                break;
            }
        }
        // Headings, quotes and list marks at its start; emphasis and code marks anywhere.
        line = System.Text.RegularExpressions.Regex.Replace(line, @"^(#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)]\s+)", "");
        line = line.Replace("**", "").Replace("__", "").Replace("`", "");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ").Trim();
        if (line.Length > max)
        {
            // At the last word that fits.
            var cut = line.LastIndexOf(' ', max - 1);
            line = line[..(cut > 0 ? cut : max - 1)].TrimEnd(',', ';', ':', ' ') + "…";
        }
        return line.Length > 0 ? line : "Done.";
    }

    private ToolCallItem FindOrAddTool(ToolCall call, string summary)
    {
        var existing = Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == call.Id);
        if (existing is not null)
            return existing;
        var item = new ToolCallItem(call.Id, call.Name, summary) { Arguments = call.Arguments };
        Items.Add(item);
        return item;
    }
}
