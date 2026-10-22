using BlackBox.Core.Agent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// A message the user sent while the agent works, which the model hasn't read yet. It waits above
/// the box, with × to take it back, and moves into the chat when the model reads it: where it then
/// stands in the conversation, after what the model was doing when it was sent.
/// </summary>
public sealed partial class QueuedMessageItem(UserInput input, Action<QueuedMessageItem> cancel) : ObservableObject
{
    /// <summary>The message as it was sent; the agent tells it from another with the same words by this instance.</summary>
    public UserInput Input { get; } = input;

    /// <summary>When it was sent: the time its bubble shows in the chat.</summary>
    public DateTime SentAt { get; } = DateTime.UtcNow;

    public string Text => Input.Text;
    public bool HasText => Input.Text.Length > 0;

    /// <summary>Where it was written, when not in the chat's box (the Filmmaking window).</summary>
    public string? Via => Input.Via;

    public bool HasVia => Input.Via is not null;

    /// <summary>The files that go with it: "cat.png", "3 files".</summary>
    public string FilesText => Input.Files.Count switch
    {
        0 => "",
        1 => Path.GetFileName(Input.Files[0].TrimEnd('\\', '/')),
        var n => $"{n} files",
    };

    public bool HasFiles => Input.Files.Count > 0;

    [RelayCommand]
    private void Cancel() => cancel(this);
}
