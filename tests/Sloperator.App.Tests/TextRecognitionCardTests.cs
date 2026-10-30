using Sloperator.App.ViewModels;
using Sloperator.Core.Platform;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The chat bar's offer of text recognition: Install, Not now and Turn off, what it says after each, and when it goes.</summary>
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
    public async Task Install_warns_of_Windows_prompt_first_then_says_how_it_went_and_goes()
    {
        var reader = new Reader(TextInstall.Installed, "English (United States)");
        var closed = 0;
        var card = new TextRecognitionOffer(reader, () => { }, () => closed++) { Linger = TimeSpan.Zero };
        Assert.True(card.IsOpen);
        Assert.True(card.ShowsWarning); // said before Install is pressed

        await card.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, reader.Installs);
        Assert.False(card.IsOpen);
        Assert.False(card.IsInstalling);
        Assert.Contains("Installed: English (United States)", card.Status);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task A_declined_prompt_says_so_and_it_is_offered_again()
    {
        var card = new TextRecognitionOffer(new Reader(TextInstall.Declined), () => { }, () => { }) { Linger = TimeSpan.Zero };
        await card.InstallCommand.ExecuteAsync(null);
        Assert.Contains("declined", card.Status);
        Assert.Contains("Offered again", card.Status);
    }

    [Fact]
    public async Task Not_now_and_turn_off_close_the_offer_and_only_turn_off_changes_the_setting()
    {
        var (turnedOff, closed) = (0, 0);
        var later = new TextRecognitionOffer(new Reader(TextInstall.Installed), () => turnedOff++, () => closed++) { Linger = TimeSpan.Zero };
        later.NotNowCommand.Execute(null);
        Assert.False(later.IsOpen);
        Assert.Equal(0, turnedOff);
        Assert.Equal(1, closed);

        var off = new TextRecognitionOffer(new Reader(TextInstall.Installed), () => turnedOff++, () => closed++) { Linger = TimeSpan.Zero };
        await off.TurnOffCommand.ExecuteAsync(null);
        Assert.False(off.IsOpen);
        Assert.Equal(1, turnedOff);
        Assert.Contains("Settings → General", off.Status);
        Assert.Equal(2, closed);
    }
}
