namespace BlackBox.Core.Platform;

/// <summary>Passes input through to the real controller and tells the overlay what was done, in words a person reads.</summary>
public sealed class OverlayInputController(IInputController inner, IInputOverlay overlay) : IInputController
{
    private const int MaxTypedChars = 40;

    public (int X, int Y) GetCursorPosition() => inner.GetCursorPosition();
    public void MoveMouse(int x, int y) => inner.MoveMouse(x, y);
    public bool IsKnownKey(string key) => inner.IsKnownKey(key);

    public void MouseDown(MouseButton button)
    {
        inner.MouseDown(button);
        Show($"{button} button down", inner.GetCursorPosition());
    }

    public void MouseUp(MouseButton button)
    {
        inner.MouseUp(button);
        Show($"{button} button up", inner.GetCursorPosition());
    }

    public void Click(MouseButton button, int count)
    {
        inner.Click(button, count);
        var kind = count switch { 1 => "click", 2 => "double click", 3 => "triple click", _ => $"click ×{count}" };
        Show($"{button} {kind}", inner.GetCursorPosition());
    }

    public void Scroll(int dx, int dy)
    {
        inner.Scroll(dx, dy);
        var parts = new List<string>();
        if (dy != 0) parts.Add($"{(dy > 0 ? "down" : "up")} {Math.Abs(dy)}");
        if (dx != 0) parts.Add($"{(dx > 0 ? "right" : "left")} {Math.Abs(dx)}");
        Show("Scroll " + string.Join(", ", parts), inner.GetCursorPosition());
    }

    public void TypeText(string text)
    {
        inner.TypeText(text);
        var shown = text.Replace("\r\n", "⏎").Replace('\n', '⏎').Replace('\t', '⇥');
        if (shown.Length > MaxTypedChars)
            shown = shown[..MaxTypedChars] + "…";
        Show($"Type “{shown}”");
    }

    public void PressKeys(IReadOnlyList<string> keys)
    {
        inner.PressKeys(keys);
        Show("Keys " + string.Join("+", keys.Select(KeyName)));
    }

    internal static string KeyName(string key) => key.Trim().ToLowerInvariant() switch
    {
        "left" or "arrowleft" => "←",
        "right" or "arrowright" => "→",
        "up" or "arrowup" => "↑",
        "down" or "arrowdown" => "↓",
        "ctrl" or "control" => "Ctrl",
        "win" or "windows" or "super" or "cmd" or "meta" => "Win",
        "esc" or "escape" => "Esc",
        "pageup" or "pgup" => "PgUp",
        "pagedown" or "pgdn" => "PgDn",
        var k when k.Length == 1 => k.ToUpperInvariant(),
        var k when k.Length > 1 && k[0] == 'f' && k[1..].All(char.IsDigit) => k.ToUpperInvariant(),
        var k => char.ToUpperInvariant(k[0]) + k[1..],
    };

    private void Show(string text, (int X, int Y)? point = null)
    {
        // The overlay is a nicety; it must never break the action that was just performed.
        try { overlay.Show(text, point); }
        catch { }
    }
}
