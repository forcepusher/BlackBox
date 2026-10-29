using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The top bar's saved servers: each one that answered, with its key and model, to switch back to.</summary>
public class SavedServerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bb-saved-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void A_server_that_answered_is_kept_with_its_key_and_model_and_switching_brings_them_back()
    {
        var server = new ServerSettings { BaseUrl = "http://localhost:1234/v1", ApiKey = "", Model = "qwen" };
        Assert.True(server.Remember());
        Assert.False(server.Remember()); // nothing new

        server.BaseUrl = "https://api.example.test/v1";
        server.ApiKey = "test-key";
        server.Model = "";
        server.AutoModel = "picked-there";
        Assert.True(server.Remember());
        Assert.Equal(["http://localhost:1234/v1", "https://api.example.test/v1"], server.Saved.Select(s => s.BaseUrl));

        server.Use(server.Saved[0]);
        Assert.Equal(("http://localhost:1234/v1", "", "qwen", (string?)null), (server.BaseUrl, server.ApiKey, server.Model, server.AutoModel));

        // A model picked there later is what switching back gives; the order stays.
        server.Model = "gemma";
        Assert.True(server.Remember());
        server.Use(server.Saved[1]);
        Assert.Equal("test-key", server.ApiKey);
        server.Use(server.Saved[0]);
        Assert.Equal("gemma", server.Model);
        Assert.Equal(["http://localhost:1234/v1", "https://api.example.test/v1"], server.Saved.Select(s => s.BaseUrl));

        server.Forget("https://api.example.test/v1");
        Assert.Equal(["http://localhost:1234/v1"], server.Saved.Select(s => s.BaseUrl));
    }

    [Fact]
    public void An_empty_address_is_not_kept()
    {
        var server = new ServerSettings { BaseUrl = " " };
        Assert.False(server.Remember());
        Assert.Empty(server.Saved);
    }

    [Fact]
    public void Saved_servers_are_saved_in_the_settings()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var settings = new AppSettings { Server = { BaseUrl = "http://localhost:1234/v1", ApiKey = "test-key", Model = "qwen" } };
        settings.Server.Remember();
        new SettingsStore(paths).Save(settings);

        var saved = Assert.Single(new SettingsStore(paths).Load().Server.Saved);
        Assert.Equal(("http://localhost:1234/v1", "test-key", "qwen"), (saved.BaseUrl, saved.ApiKey, saved.Model));
    }
}
