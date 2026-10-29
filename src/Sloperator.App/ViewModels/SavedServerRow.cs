using Sloperator.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Sloperator.App.ViewModels;

/// <summary>A row of the saved-servers dropdown beside the address: picking it switches to that server, its trash button forgets it.</summary>
public sealed partial class SavedServerRow(SavedServer saved, bool isCurrent, Action<SavedServer> use, Action<SavedServer> forget)
{
    public string Address { get; } = saved.BaseUrl;

    /// <summary>The model picked there, else that Sloperator picks one.</summary>
    public string Details { get; } = saved.Model is { Length: > 0 } model ? model : "Model picked by Sloperator";

    /// <summary>The server in use; its row is highlighted.</summary>
    public bool IsCurrent { get; } = isCurrent;

    [RelayCommand]
    private void Use() => use(saved);

    [RelayCommand]
    private void Forget() => forget(saved);
}
