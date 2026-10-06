using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Llm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public abstract class ChatItemViewModel : ObservableObject;

public sealed class UserMessageItem(string text) : ChatItemViewModel
{
    public string Text { get; } = text;
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

    /// <summary>
    /// Under a reply in progress: "…", or while nothing has come yet, for how long. A server
    /// reading a long prompt sends nothing for minutes; the count shows BlackBox is waiting, not stuck.
    /// </summary>
    public string TypingText => !HasText && !HasReasoning && DateTime.UtcNow - _started is { TotalSeconds: >= 3 } waited
        ? $"Waiting for the model… {waited:m\\:ss}"
        : "…";

    /// <summary>Called every second while a run is on, for <see cref="TypingText"/>.</summary>
    public void Tick() => OnPropertyChanged(nameof(TypingText));

    // Streams arrive with blank lines around them (thinking usually ends in "\n\n" until the
    // reply starts, and the reply opens with them), which showed as empty bands mid-stream.
    public string ShownText => Text.Trim();
    public string ShownReasoning => Reasoning.Trim();
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
    public string ThinkingLabel => IsStreaming && !HasText ? "Thinking…" : "Thoughts";

    /// <summary>The "…" under a reply in progress; while the Thoughts header already says "Thinking…" it would only be a stray line.</summary>
    public bool ShowTypingDots => IsStreaming && (HasText || !HasReasoning);
}

public enum ToolItemState { AwaitingApproval, Running, Done, Error, Denied }

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

    public string StatusText => State switch
    {
        ToolItemState.AwaitingApproval => "Waiting for your OK",
        ToolItemState.Running => "Running…",
        ToolItemState.Done => "Done",
        ToolItemState.Error => "Failed",
        ToolItemState.Denied => "Declined",
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

public sealed class NoticeItem(string text, bool isError) : ChatItemViewModel
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}
