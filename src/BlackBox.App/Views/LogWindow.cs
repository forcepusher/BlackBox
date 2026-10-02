using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using BlackBox.Core.Llm;

namespace BlackBox.App.Views;

/// <summary>Live view of the llama-server output.</summary>
public sealed class LogWindow : Window
{
    private readonly LlamaServerHost _server;
    private readonly TextBox _text;
    private readonly DispatcherTimer _timer;
    private bool _dirty = true;

    public LogWindow(LlamaServerHost server)
    {
        _server = server;
        Title = "llama-server log";
        Width = 900;
        Height = 520;
        _text = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 12,
        };
        Content = _text;

        // Server output can be very chatty while loading; refresh a few times a second at most.
        _server.LogLine += OnLogLine;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _server.LogLine -= OnLogLine;
        };
    }

    private void OnLogLine(string _) => _dirty = true;

    private void Refresh()
    {
        if (!_dirty) return;
        _dirty = false;
        _text.Text = _server.RecentLog;
        _text.CaretIndex = _text.Text?.Length ?? 0;
    }
}
