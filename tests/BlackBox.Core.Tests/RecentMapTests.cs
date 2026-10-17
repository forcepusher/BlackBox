using Xunit;

namespace BlackBox.Core.Tests;

public class RecentMapTests
{
    [Fact]
    public void Only_the_entries_set_last_are_kept()
    {
        // ComfyUI's jobs and the documents written were kept for the process's life and only grew.
        var map = new RecentMap<string, int>(3, StringComparer.OrdinalIgnoreCase);
        map.Set("a", 1);
        map.Set("b", 2);
        map.Set("c", 3);
        map.Set("A", 10); // set again, so the newest
        map.Set("d", 4);  // and "b", the oldest now, goes

        Assert.Equal(3, map.Count);
        Assert.False(map.TryGet("b", out _));
        Assert.True(map.TryGet("a", out var a));
        Assert.Equal(10, a);
        Assert.True(map.TryGet("c", out _));
        Assert.True(map.TryGet("d", out _));
        map.Remove("c");
        Assert.Equal(2, map.Count);
    }
}
