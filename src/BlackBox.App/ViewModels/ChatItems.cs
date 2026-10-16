using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Llm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public abstract class ChatItemViewModel : ObservableObject
{
    /// <summary>Files shown under the item: what a tool made or showed, or what a reply names by its path.</summary>
    public System.Collections.ObjectModel.ObservableCollection<FilePreview> Previews { get; } = [];

    public bool HasPreviews => Previews.Count > 0;

    public void AddPreviews(IEnumerable<string> paths, PreviewServices services)
    {
        foreach (var path in paths)
            if (!Previews.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
                Previews.Add(FilePreview.For(path, services));
        OnPropertyChanged(nameof(HasPreviews));
    }

    /// <summary>Stops players and lets go of decoded frames, when the item leaves the chat.</summary>
    public virtual void DisposePreviews()
    {
        foreach (var p in Previews)
            p.Dispose();
    }
}

/// <summary>A message the user sent: what they typed, and the files that came with it (dropped, pasted or picked).</summary>
public sealed class UserMessageItem(string text, IReadOnlyList<string>? files = null) : ChatItemViewModel
{
    public string Text { get; } = text;
    public bool HasText => Text.Length > 0;

    public IReadOnlyList<AttachmentItem> Attachments { get; } = (files ?? []).Select(f => new AttachmentItem(f)).ToList();
    public bool HasAttachments => Attachments.Count > 0;

    public List<string> Files => Attachments.Select(a => a.Path).ToList();

    public override void DisposePreviews()
    {
        base.DisposePreviews();
        foreach (var a in Attachments)
            a.Dispose();
    }
}

public sealed partial class AssistantMessageItem : ChatItemViewModel
{
    private readonly object _gate = new();
    private readonly StringBuilder _newText = new(), _newReasoning = new();
    private bool _flushQueued;

    /// <summary>
    /// Takes a streamed piece, on any thread. The pieces that arrive until the window is idle are
    /// shown together: one update per piece laid out the whole growing text again each time, and
    /// a long reply froze the window for minutes (6,000 pieces: 139 s).
    /// </summary>
    public void Append(StreamDelta delta)
    {
        lock (_gate)
        {
            (delta.Channel == StreamChannel.Reasoning ? _newReasoning : _newText).Append(delta.Text);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        string text, reasoning;
        lock (_gate)
        {
            (text, reasoning) = (_newText.ToString(), _newReasoning.ToString());
            _newText.Clear();
            _newReasoning.Clear();
            _flushQueued = false;
        }
        if (!IsStreaming)
            return; // the finished message has replaced what streamed
        if (reasoning.Length > 0) Reasoning += reasoning;
        if (text.Length > 0) Text += text;
    }

    /// <summary>
    /// Ends the stream: the finished message replaces what streamed. Without one (a reply thrown
    /// away, or the run ended), what streamed stays as it is.
    /// </summary>
    public void Finish(ChatMessage? message = null)
    {
        Flush();
        if (message is not null)
        {
            Text = (message.Content ?? "").Trim();
            Reasoning = (message.Reasoning ?? Reasoning).Trim();
        }
        IsStreaming = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(ShownText), nameof(ThinkingLabel), nameof(ShowTypingDots), nameof(TypingText))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning), nameof(ShownReasoning), nameof(ShowTypingDots), nameof(TypingText))]
    private string _reasoning = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThinkingLabel), nameof(ShowTypingDots))]
    private bool _isStreaming = true;

    private readonly DateTime _started = DateTime.UtcNow;

    /// <summary>How much of the step's request has gone out to the server; set from the agent's thread.</summary>
    private long _sent, _total;

    /// <summary>The request on its way out: <paramref name="sent"/> of <paramref name="total"/> bytes; shown by the next <see cref="Tick"/>.</summary>
    public void Sending(long sent, long total)
    {
        Interlocked.Exchange(ref _total, total);
        Interlocked.Exchange(ref _sent, sent);
    }

    /// <summary>
    /// Under a reply in progress: "…", or while nothing has come yet, for how long. A server
    /// reading a long prompt sends nothing for minutes; the count shows BlackBox is waiting, not
    /// stuck. Before that the request has to get there, which over a slow link takes minutes too.
    /// </summary>
    public string TypingText
    {
        get
        {
            if (HasText || HasReasoning || DateTime.UtcNow - _started is not { TotalSeconds: >= 3 } waited)
                return "…";
            var (sent, total) = (Interlocked.Read(ref _sent), Interlocked.Read(ref _total));
            return total > 0 && sent < total
                ? $"Sending to the server… {sent / 1048576.0:0.0} of {total / 1048576.0:0.0} MB, {waited:m\\:ss}"
                : $"Waiting for the model… {waited:m\\:ss}";
        }
    }

    /// <summary>Called every second while a run is on, for <see cref="TypingText"/>.</summary>
    public void Tick() => OnPropertyChanged(nameof(TypingText));

    // Streams arrive with blank lines around them (thinking usually ends in "\n\n" until the
    // reply starts, and the reply opens with them), which showed as empty bands mid-stream.
    public string ShownText => Text.Trim();

    /// <summary>The thinking as shown; Gemma 4's comes with its channel's name in front ("thought\n…"), which says nothing.</summary>
    public string ShownReasoning => Reasoning.Trim() is var r && r.StartsWith("thought\n", StringComparison.Ordinal) ? r[8..].TrimStart() : Reasoning.Trim();
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
    public string ThinkingLabel => IsStreaming && !HasText ? "Thinking…" : "Thoughts";

    /// <summary>The "…" under a reply in progress; while the Thoughts header already says "Thinking…" it would only be a stray line.</summary>
    public bool ShowTypingDots => IsStreaming && (HasText || !HasReasoning);
}

/// <summary>Stopped: the run ended (Pause/Break, Stop, an error) while the call was running or waiting for an OK.</summary>
public enum ToolItemState { AwaitingApproval, Running, Done, Error, Denied, Stopped }

public sealed partial class ToolCallItem(string callId, string name, string summary) : ChatItemViewModel
{
    private TaskCompletionSource<bool>? _approval;

    public string CallId { get; } = callId;
    public string Name { get; } = name;

    [ObservableProperty]
    private string _summary = summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAwaitingApproval), nameof(StatusText), nameof(IsError))]
    private ToolItemState _state = ToolItemState.Running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenshot))]
    private Bitmap? _screenshot;

    public bool IsAwaitingApproval => State == ToolItemState.AwaitingApproval;
    public bool IsError => State is ToolItemState.Error or ToolItemState.Denied;
    public bool HasResult => ResultText.Length > 0;
    public bool HasScreenshot => Screenshot is not null;

    /// <summary>How a long call is getting on ("KSampler, step 3 of 20 · 1:12"), while it runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _progress = "";

    public string StatusText => State switch
    {
        ToolItemState.AwaitingApproval => "Waiting for your OK",
        ToolItemState.Running => Progress.Length > 0 ? Progress : "Running…",
        ToolItemState.Done => "Done",
        ToolItemState.Error => "Failed",
        ToolItemState.Denied => "Declined",
        ToolItemState.Stopped => "Stopped",
        _ => "",
    };

    public Task<bool> WaitForApprovalAsync(CancellationToken ct)
    {
        _approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => _approval.TrySetResult(false));
        State = ToolItemState.AwaitingApproval;
        return _approval.Task;
    }

    [RelayCommand]
    private void Approve()
    {
        State = ToolItemState.Running;
        _approval?.TrySetResult(true);
    }

    [RelayCommand]
    private void Deny()
    {
        State = ToolItemState.Denied;
        _approval?.TrySetResult(false);
    }
}

/// <param name="carryOn">For the notice a stopped run leaves ("Stopped with Pause/Break."): carries on with the task. Null for any other notice.</param>
/// <param name="unsent">The run stopped before the model had the user's message: carrying on sends that message again.</param>
public sealed partial class NoticeItem(string text, bool isError, Action? carryOn = null, bool unsent = false) : ChatItemViewModel
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;

    /// <summary>A run ended here before the task was done (stopped, or an error it couldn't get past).</summary>
    public bool Stopped { get; } = carryOn is not null;

    /// <summary>The run stopped before the model had the user's message: Continue sends that message again.</summary>
    public bool Unsent { get; } = unsent;

    /// <summary>Its Continue button shows: until the chat goes on, by Continue or a message of the user's.</summary>
    [ObservableProperty]
    private bool _canContinue = carryOn is not null;

    /// <summary>The run it starts takes the button off (and every other one); with a run still going it does nothing.</summary>
    [RelayCommand]
    private void Continue() => carryOn?.Invoke();
}
