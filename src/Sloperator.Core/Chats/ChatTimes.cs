using System.Globalization;

namespace Sloperator.Core.Chats;

/// <summary>
/// How the chat writes times: when each line happened, and how long a reply, a tool call or a
/// whole run took, so where a task's minutes went can be read off the chat.
/// </summary>
public static class ChatTimes
{
    /// <summary>The time of day, local: "14:05:09".</summary>
    public static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The date and time in full, for a tooltip: "Friday, October 16, 2026, 14:05:09".</summary>
    public static string Full(DateTime utc) => utc.ToLocalTime().ToString("dddd, MMMM d, yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>How long something took: "35 ms", "3.2 s", "41 s", "4 min 5 s", "2 h 13 min". Cut down to the unit shown, never rounded up.</summary>
    public static string Duration(TimeSpan took)
    {
        if (took < TimeSpan.Zero)
            took = TimeSpan.Zero; // the clock was set back meanwhile
        if (took < TimeSpan.FromSeconds(1))
            return $"{(int)took.TotalMilliseconds} ms";
        if (took < TimeSpan.FromSeconds(10))
        {
            var tenths = took.Ticks / (TimeSpan.TicksPerSecond / 10);
            return $"{tenths / 10}.{tenths % 10} s";
        }
        if (took < TimeSpan.FromMinutes(1))
            return $"{(int)took.TotalSeconds} s";
        if (took < TimeSpan.FromHours(1))
            return took.Seconds == 0 ? $"{(int)took.TotalMinutes} min" : $"{(int)took.TotalMinutes} min {took.Seconds} s";
        return took.Minutes == 0 ? $"{(int)took.TotalHours} h" : $"{(int)took.TotalHours} h {took.Minutes} min";
    }

    /// <summary>How long something has been going, as a stopwatch shows it: "0:41", "12:05", "1:02:03".</summary>
    public static string Stopwatch(TimeSpan running)
    {
        if (running < TimeSpan.Zero)
            running = TimeSpan.Zero;
        return running.TotalHours >= 1
            ? $"{(int)running.TotalHours}:{running.Minutes:00}:{running.Seconds:00}"
            : $"{(int)running.TotalMinutes}:{running.Seconds:00}";
    }
}
