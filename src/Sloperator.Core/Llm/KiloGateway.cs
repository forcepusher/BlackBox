namespace Sloperator.Core.Llm;

/// <summary>
/// Kilo's AI gateway, which serves its free models to anyone, without an account or a key: where
/// Sloperator starts, so it works without a model server of one's own (and what the "Use Kilo's free
/// models" button goes back to). Free use is 200 requests an hour per IP address, a step being one
/// and a click one more, and which models are free changes over time; without a key only those are
/// listed (see <see cref="ServerProbe"/>).
/// </summary>
public static class KiloGateway
{
    public const string Address = "https://ai-gateway.kilo.ai/api/v1";

    /// <summary>
    /// The free model Sloperator uses there while none is picked. Of the five free ones that took screenshots and called
    /// tools on 2026-10-16, Nemotron 3.5 Super VL (120B, 12B active) hit 12 of 16 targets in two runs
    /// of ClickAccuracyTests (buttons, links, similar, ultrawide) on the 0-1000 grid, every miss within
    /// 16 px of a small link or button. Dots3-Note Preview hit 0 of 4, Nemotron 3 Nano Omni 1 of 4 (it
    /// gives fractions of the screen), and Inkling Small had used up the daily allowance Kilo shares
    /// among everyone.
    /// </summary>
    public const string Model = "nvidia/nemotron-3.5-super-vl:free";

    /// <summary>Whether the address is Kilo's gateway (or its older api.kilo.ai address).</summary>
    public static bool Is(string? address) =>
        ServerAddress.Candidates(address ?? "").FirstOrDefault() is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("ai-gateway.kilo.ai", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("api.kilo.ai", StringComparison.OrdinalIgnoreCase));
}
