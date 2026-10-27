using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

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
    public void A_manual_size_replaces_the_detected_one_even_above_what_the_server_reports()
    {
        var server = new ServerSettings { AutoContext = false, ManualContextSize = 32 * 1024 };
        server.SetDetectedContext(131072);
        Assert.Equal(32 * 1024, server.ContextSize);

        server.ManualContextSize = 300_000; // typed past the slider's end: used as typed
        Assert.Equal(300_000, server.ContextSize);

        server.AutoContext = true;
        Assert.Equal(131072, server.ContextSize);
    }

    [Fact]
    public void Of_a_hosted_services_maximum_auto_uses_100k_and_a_manual_size_can_go_beyond()
    {
        // Kilo lists 1M for its free Nemotron: at that size a request would grow far past the 20 MB it takes.
        var server = new ServerSettings();
        server.SetDetectedContext(1_000_000, hosted: true);
        Assert.Equal(ServerSettings.HostedContextSize, server.ContextSize);
        Assert.True(server.ContextReported);
        Assert.Equal(1_000_000, server.DetectedContextSize); // where the slider ends

        server.SetDetectedContext(65536, hosted: true); // a smaller maximum is used whole
        Assert.Equal(65536, server.ContextSize);

        server.SetDetectedContext(1_000_000, hosted: true);
        server.AutoContext = false;
        server.ManualContextSize = 512 * 1024;
        Assert.Equal(512 * 1024, server.ContextSize);

        server.AutoContext = true;
        server.SetDetectedContext(1_000_000); // a server of one's own that runs the model with 1M
        Assert.Equal(1_000_000, server.ContextSize);
    }

    [Fact]
    public void Unticking_auto_without_ever_picking_a_size_keeps_the_detected_one()
    {
        var server = new ServerSettings { AutoContext = false };
        server.SetDetectedContext(65536);
        Assert.Equal(65536, server.ContextSize);
    }

    [Fact]
    public void A_too_long_error_lowers_the_auto_size_for_the_next_run_but_leaves_a_manual_one()
    {
        var server = new ServerSettings();
        server.LearnContextLimit(200_000); // no news: the session kept the size it was given
        Assert.Equal(ServerSettings.FallbackContextSize, server.ContextSize);
        server.LearnContextLimit(65536);
        Assert.Equal(65536, server.ContextSize);

        var manual = new ServerSettings { AutoContext = false, ManualContextSize = 128 * 1024 };
        manual.LearnContextLimit(65536);
        Assert.Equal(128 * 1024, manual.ContextSize); // the user's choice; the run itself drops to the server's size
        Assert.Equal(65536, manual.DetectedContextSize);
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
    public void Old_settings_files_start_on_auto_and_lose_the_vision_switch()
    {
        var root = Directory.CreateTempSubdirectory("bb-context-").FullName;
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.DataDir);
        File.WriteAllText(paths.SettingsFile, """{ "server": { "baseUrl": "http://host:1234/v1", "apiKey": "k", "model": "qwen", "supportsVision": false } }""");

        var settings = new SettingsStore(paths).Load();

        Assert.True(settings.Server.AutoContext);
        Assert.Equal(0, settings.Server.ManualContextSize);
        Assert.Equal("k", settings.Server.ApiKey);
        Assert.True(settings.Server.SupportsVision); // read from the server on connecting now
        Assert.DoesNotContain("supportsVision", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void A_section_written_as_null_gets_its_defaults()
    {
        var root = Directory.CreateTempSubdirectory("bb-context-").FullName;
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.DataDir);
        File.WriteAllText(paths.SettingsFile, """{ "server": null, "agent": null }""");

        var settings = new SettingsStore(paths).Load();

        Assert.Equal(new ServerSettings().BaseUrl, settings.Server.BaseUrl);
        Assert.NotNull(settings.Agent);
    }

    [Fact]
    public void The_slider_goes_up_to_a_million_with_the_servers_size_on_it()
    {
        var all = ContextSizes.Stops(null);
        Assert.Equal(4096, all[0]);
        Assert.Equal(1024 * 1024, all[^1]);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First < p.Second));

        // LM Studio's 100k takes 96k's place, and the slider goes on to 1M.
        var lmStudio = ContextSizes.Stops(100_000);
        Assert.Equal(1024 * 1024, lmStudio[^1]);
        Assert.Contains(100_000, lmStudio);
        Assert.DoesNotContain(96 * 1024, lmStudio);
        Assert.True(lmStudio.Zip(lmStudio.Skip(1)).All(p => p.First < p.Second));

        var exact = ContextSizes.Stops(65536);
        Assert.Single(exact, s => s == 65536); // no duplicate 64k stop

        // Claude's 1,000,000 is the end, not a second "1M" beside 1,048,576.
        Assert.Equal(1_000_000, ContextSizes.Stops(1_000_000)[^1]);
        Assert.Equal(2_000_000, ContextSizes.Stops(2_000_000)[^1]);
        Assert.Equal(2048, ContextSizes.Stops(2048)[0]); // a tiny server's size is on it too
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

    [Theory]
    [InlineData("65536", 65536)]
    [InlineData("64k", 65536)]      // a slider stop: counted in 1024s
    [InlineData(" 64 K ", 65536)]
    [InlineData("40k", 40960)]
    [InlineData("250k", 250_000)]   // not a stop: as LM Studio would have it
    [InlineData("100k", 100_000)]
    [InlineData("250,000", 250_000)]
    [InlineData("1M", 1024 * 1024)]
    [InlineData("1.5m", 1536 * 1024)]
    [InlineData("100", ContextSizes.Min)]           // too small to work with
    [InlineData("999999999", ContextSizes.Max)]
    public void Typed_sizes_are_read(string text, int tokens) =>
        Assert.Equal(tokens, ContextSizes.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("-5k")]
    [InlineData("0")]
    [InlineData("k")]
    public void Things_that_are_not_sizes_are_refused(string text) =>
        Assert.Null(ContextSizes.Parse(text));

    [Fact]
    public void What_is_written_reads_back_the_same()
    {
        foreach (var size in ContextSizes.Standard.Concat([100_000, 250_000, 131072]))
            Assert.Equal(size, ContextSizes.Parse(ContextSizes.Format(size)));
    }
}
