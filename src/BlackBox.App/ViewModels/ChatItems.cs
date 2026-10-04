using Avalonia.Media.Imaging;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(ShownText), nameof(ThinkingLabel), nameof(ShowTypingDots))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning), nameof(ShownReasoning), nameof(ShowTypingDots))]
    private string _reasoning = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThinkingLabel), nameof(ShowTypingDots))]
    private bool _isStreaming = true;

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
