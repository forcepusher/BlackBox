using BlackBox.Core.Chats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>A row of the History dropdown: opens the chat, or deletes it with the trash button.</summary>
public sealed partial class PastChat(ChatHeader header, Action<PastChat> open, Action<PastChat> delete) : ObservableObject
{
    public string Id { get; } = header.Id;
    public string Title { get; } = header.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(When))]
    private DateTime _updated = header.Updated;

    /// <summary>The chat on screen; its row is highlighted.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>"14:05" today, "Yesterday", "Oct 3" this year, else "Oct 3, 2025".</summary>
    public string When
    {
        get
        {
            var local = Updated.ToLocalTime();
            var today = DateTime.Today;
            if (local.Date == today) return local.ToString("HH:mm");
            if (local.Date == today.AddDays(-1)) return "Yesterday";
            return local.Year == today.Year ? local.ToString("MMM d") : local.ToString("MMM d, yyyy");
        }
    }

    [RelayCommand]
    private void Open() => open(this);

    [RelayCommand]
    private void Delete() => delete(this);
}
