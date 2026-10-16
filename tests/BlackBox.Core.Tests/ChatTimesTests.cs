using BlackBox.Core.Chats;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>The times in the chat: when each line happened, and how long a step, a tool call or a run took.</summary>
public class ChatTimesTests
{
    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(35, "35 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1_000, "1.0 s")]
    [InlineData(3_299, "3.2 s")]
    [InlineData(9_999, "9.9 s")]
    [InlineData(10_000, "10 s")]
    [InlineData(41_900, "41 s")]
    [InlineData(60_000, "1 min")]
    [InlineData(245_700, "4 min 5 s")]
    [InlineData(3_599_999, "59 min 59 s")]
    [InlineData(3_600_000, "1 h")]
    [InlineData(8_020_000, "2 h 13 min")]
    [InlineData(-5_000, "0 ms")]
    public void A_duration_is_cut_down_to_the_unit_it_shows(long milliseconds, string shown) =>
        Assert.Equal(shown, ChatTimes.Duration(TimeSpan.FromMilliseconds(milliseconds)));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(41_500, "0:41")]
    [InlineData(725_000, "12:05")]
    [InlineData(3_723_000, "1:02:03")]
    public void A_count_reads_like_a_stopwatch(long milliseconds, string shown) =>
        Assert.Equal(shown, ChatTimes.Stopwatch(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void The_time_of_day_is_local()
    {
        var local = new DateTime(2026, 10, 16, 9, 5, 7, DateTimeKind.Local);
        Assert.Equal("09:05:07", ChatTimes.Clock(local.ToUniversalTime()));
        Assert.Equal("Friday, October 16, 2026, 09:05:07", ChatTimes.Full(local.ToUniversalTime()));
    }
}
