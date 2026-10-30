using Sloperator.App.ViewModels;
using Sloperator.Core.Platform;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The card that offers text recognition: Install, Not now and Turn off, and what it says after each.</summary>
public sealed class TextRecognitionCardTests
{
    private sealed class Reader(TextInstall result, params string[] languages) : ITextReader
    {
        public int Installs;
        public IReadOnlyList<TextLine>? Read(RawImage image) => null;
        public IReadOnlyList<string> Languages() => languages;
        public Task<TextInstall> InstallAsync(CancellationToken ct) { Installs++; return Task.FromResult(result); }
    }

    [Fact]
    public async Task Install_warns_of_Windows_prompt_first_then_says_how_it_went()
    {
        var reader = new Reader(TextInstall.Installed, "English (United States)");
        var card = new TextRecognitionItem(reader, () => { });
        Assert.True(card.IsOpen);
        Assert.True(card.ShowsWarning); // said before Install is pressed

        await card.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, reader.Installs);
        Assert.False(card.IsOpen);
        Assert.False(card.IsInstalling);
        Assert.Contains("Installed: English (United States)", card.Status);
    }

    [Fact]
    public async Task A_declined_prompt_says_so_and_it_is_offered_again()
    {
        var card = new TextRecognitionItem(new Reader(TextInstall.Declined), () => { });
        await card.InstallCommand.ExecuteAsync(null);
        Assert.Contains("declined", card.Status);
        Assert.Contains("offered again", card.Status);
    }

    [Fact]
    public void Not_now_and_turn_off_close_the_offer_and_only_turn_off_changes_the_setting()
    {
        var turnedOff = 0;
        var later = new TextRecognitionItem(new Reader(TextInstall.Installed), () => turnedOff++);
        later.NotNowCommand.Execute(null);
        Assert.False(later.IsOpen);
        Assert.Equal(0, turnedOff);

        var off = new TextRecognitionItem(new Reader(TextInstall.Installed), () => turnedOff++);
        off.TurnOffCommand.Execute(null);
        Assert.False(off.IsOpen);
        Assert.Equal(1, turnedOff);
        Assert.Contains("Settings → Advanced", off.Status);
    }
}
