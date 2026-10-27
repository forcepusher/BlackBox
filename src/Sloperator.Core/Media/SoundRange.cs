using System.Globalization;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// A stretch of a sound picked in the File inspector: how it is named on its card ("0.80-1.45 s"),
/// what the model is told of it, and the picture of it the model gets (it can't hear).
/// </summary>
public static class SoundRange
{
    /// <summary>
    /// A time as the panel shows it: "0.812 s" under a minute, "1:02.345" from one on, with
    /// <paramref name="decimals"/> places.
    /// </summary>
    /// <param name="minutes">In minutes and seconds under a minute too ("0:59.00"), beside a time past one.</param>
    public static string Clock(double seconds, int decimals = 3, bool minutes = false)
    {
        seconds = Math.Max(0, seconds);
        var format = decimals > 0 ? "0." + new string('0', decimals) : "0";
        if (seconds < 60 && !minutes)
            return seconds.ToString(format, CultureInfo.InvariantCulture) + " s";
        var whole = (int)(seconds / 60);
        var rest = seconds - whole * 60;
        return $"{whole}:{rest.ToString((decimals > 0 ? "00." + new string('0', decimals) : "00"), CultureInfo.InvariantCulture)}";
    }

    /// <summary>"0.80-1.45 s", "1:02.30-1:05.10"; to the thousandth for a stretch under a tenth of a second.</summary>
    public static string Label(double from, double to)
    {
        var decimals = to - from < 0.1 ? 3 : 2;
        if (to < 60)
            return $"{Clock(from, decimals)[..^2]}-{Clock(to, decimals)}";
        return $"{Clock(from, decimals, minutes: true)}-{Clock(to, decimals)}";
    }

    /// <summary>
    /// What the model is told of the stretch from <paramref name="from"/> to <paramref name="to"/>
    /// seconds of <paramref name="mono"/>: where it is, to the sample, and how it sounds there.
    /// </summary>
    public static string Told(float[] mono, int rate, double from, double to)
    {
        var (a, b) = Samples(mono, rate, from, to);
        var stats = SoundStats.Of(mono[a..b], rate);
        var inv = CultureInfo.InvariantCulture;
        return string.Create(inv, $"They picked {from:0.000}-{to:0.000} s of it ({to - from:0.000} s long; samples {a:N0}-{b:N0} at {rate:N0} Hz, counted from 0): there it is {stats.Describe()}");
    }

    /// <summary>The stretch's waveform over its spectrogram, its marks in the sound's own seconds.</summary>
    public static SKBitmap Picture(float[] mono, int rate, double from, double to)
    {
        var (a, b) = Samples(mono, rate, from, to);
        return MediaPreview.SoundPicture(mono, rate, a, b);
    }

    private static (int From, int To) Samples(float[] mono, int rate, double from, double to)
    {
        var a = Math.Clamp((int)Math.Floor(from * rate), 0, Math.Max(0, mono.Length - 1));
        var b = Math.Clamp((int)Math.Ceiling(to * rate), a + 1, Math.Max(a + 1, mono.Length));
        return (a, Math.Min(b, mono.Length));
    }
}
