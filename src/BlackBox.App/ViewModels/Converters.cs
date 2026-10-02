using Avalonia.Data.Converters;
using Avalonia.Media;

namespace BlackBox.App.ViewModels;

public static class Converters
{
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#E06C6C"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8B9199"));

    public static readonly IValueConverter ErrorBrush =
        new FuncValueConverter<bool, IBrush>(isError => isError ? Error : Muted);
}
