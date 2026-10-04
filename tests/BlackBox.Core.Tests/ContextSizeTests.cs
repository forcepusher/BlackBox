using BlackBox.Core.Settings;
using Xunit;

namespace BlackBox.Core.Tests;

public class ContextSizeTests
{
    [Fact]
    public void Auto_uses_what_the_server_reports_or_the_guess()
    {
        var server = new ServerSettings();
        Assert.True(server.AutoContext);
        Assert.Equal(ServerSettings.FallbackContextSize, server.ContextSize);
        Assert.False(server.ContextReported);

        server.SetDetectedContext(40960);
        Assert.Equal(40960, server.ContextSize);
        Assert.True(server.ContextReported);

        server.SetDetectedContext(null); // another model the server says nothing about
        Assert.Equal(ServerSettings.FallbackContextSize, server.ContextSize);
        Assert.False(server.ContextReported);
    }

    [Fact]
    public void A_manual_size_replaces_the_detected_one_but_never_exceeds_what_the_server_reports()
    {
        var server = new ServerSettings { AutoContext = false, ManualContextSize = 32 * 1024 };
        server.SetDetectedContext(131072);
        Assert.Equal(32 * 1024, server.ContextSize);

        server.ManualContextSize = 256 * 1024;
        Assert.Equal(131072, server.ContextSize);

        server.SetDetectedContext(null); // unknown: the manual size is all there is to go on
        Assert.Equal(256 * 1024, server.ContextSize);

        server.AutoContext = true;
        Assert.Equal(ServerSettings.FallbackContextSize, server.ContextSize);
    }

    [Fact]
    public void Unticking_auto_without_ever_picking_a_size_keeps_the_detected_one()
    {
        var server = new ServerSettings { AutoContext = false };
        server.SetDetectedContext(65536);
        Assert.Equal(65536, server.ContextSize);
    }

    [Fact]
    public void A_too_long_error_lowers_the_size_for_the_next_run_but_nothing_raises_it()
    {
        var server = new ServerSettings { AutoContext = false, ManualContextSize = 128 * 1024 };
        server.LearnContextLimit(200_000); // no news: the session kept the size it was given
        Assert.Equal(128 * 1024, server.ContextSize);

        server.LearnContextLimit(65536);
        Assert.Equal(65536, server.ContextSize);
        Assert.Equal(128 * 1024, server.ManualContextSize); // the choice is kept for when the server runs larger again
    }

    [Fact]
    public void The_manual_choice_is_saved_and_the_detected_size_is_not()
    {
        var root = Directory.CreateTempSubdirectory("bb-context-").FullName;
        var store = new SettingsStore(new AppPaths(root));
        var settings = new AppSettings();
        settings.Server.AutoContext = false;
        settings.Server.ManualContextSize = 48 * 1024;
        settings.Server.ApiKey = "sk-test";
        settings.Server.SetDetectedContext(131072);
        store.Save(settings);

        var loaded = store.Load();

        Assert.False(loaded.Server.AutoContext);
        Assert.Equal(48 * 1024, loaded.Server.ManualContextSize);
        Assert.Equal("sk-test", loaded.Server.ApiKey);
        Assert.Equal(ServerSettings.FallbackContextSize, loaded.Server.DetectedContextSize);
        Assert.False(loaded.Server.ContextReported);
    }

    [Fact]
    public void Old_settings_files_start_on_auto()
    {
        var root = Directory.CreateTempSubdirectory("bb-context-").FullName;
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.DataDir);
        File.WriteAllText(paths.SettingsFile, """{ "server": { "baseUrl": "http://host:1234/v1", "apiKey": "k", "model": "qwen" } }""");

        var settings = new SettingsStore(paths).Load();

        Assert.True(settings.Server.AutoContext);
        Assert.Equal(0, settings.Server.ManualContextSize);
        Assert.Equal("k", settings.Server.ApiKey);
    }

    [Fact]
    public void The_slider_ends_at_the_servers_size_when_it_reports_one()
    {
        var all = ContextSizes.Stops(null);
        Assert.Equal(4096, all[0]);
        Assert.Equal(1024 * 1024, all[^1]);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First < p.Second));

        var upTo100k = ContextSizes.Stops(100_000);
        Assert.Equal(100_000, upTo100k[^1]);
        Assert.Equal(96 * 1024, upTo100k[^2]);

        var exact = ContextSizes.Stops(65536);
        Assert.Equal(65536, exact[^1]);
        Assert.Equal(48 * 1024, exact[^2]); // no duplicate 64k stop

        Assert.Equal([2048], ContextSizes.Stops(2048)); // a tiny server still gets a working slider
    }

    [Theory]
    [InlineData(4096, 0)]
    [InlineData(1000, 0)]     // below the first stop
    [InlineData(40960, 6)]    // exactly 40k
    [InlineData(100_000, 10)] // between 96k and 128k: the stop below
    [InlineData(2_000_000, 20)]
    public void Sizes_sit_on_the_stop_at_or_below_them(int tokens, int index) =>
        Assert.Equal(index, ContextSizes.IndexOf(ContextSizes.Standard, tokens));

    [Theory]
    [InlineData(4096, "4k")]
    [InlineData(131072, "128k")]
    [InlineData(40960, "40k")]
    [InlineData(100_000, "100k")] // as typed into LM Studio, not 97.7 × 1024
    [InlineData(250_000, "250k")]
    [InlineData(1024 * 1024, "1M")]
    [InlineData(1_000_000, "1M")]
    public void Sizes_are_written_in_k(int tokens, string text) =>
        Assert.Equal(text, ContextSizes.Format(tokens));
}
