using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- updates ----------------------------------------------------------------------

    /// <summary>Sloperator updating itself: the line under the top rows says a new version is ready or out.</summary>
    public Updater Updater => _windows.Updater;

    // Restart waits for every window's run, and so does an update check that came due.
    partial void OnIsBusyChanged(bool value) => _windows.BusyChanged();

    private bool CanRestart() => !_windows.AnyBusy();

    /// <summary>Restarts into the downloaded version, with the windows' chats open again.</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart() => _windows.Restart(this);

    /// <summary>The new release's page, with what's new in it, in the browser.</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        if (Updater.ReleasePage is not { } page)
            return;
        try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Opens the saved chat <paramref name="id"/>, as its row in History does: after a restart into an update.</summary>
    internal void OpenSaved(string id)
    {
        if (PastChats.FirstOrDefault(r => r.Id == id) is { } row)
            OpenChat(row);
    }
}
