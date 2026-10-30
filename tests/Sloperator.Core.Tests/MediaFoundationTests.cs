#if WINDOWS
using Sloperator.Platform.Windows;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Sound read by Windows' own Media Foundation, through its COM interfaces' tables (ComAbi).</summary>
public class MediaFoundationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-mf-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_wave_file_is_probed_and_read()
    {
        // One second of a 440 Hz tone, 16-bit stereo at 22,050 Hz, at half volume.
        const int rate = 22050, channels = 2;
        var path = Path.Combine(_dir, "tone ü.wav");
        using (var file = new BinaryWriter(File.Create(path)))
        {
            var data = rate * channels * 2;
            file.Write("RIFF"u8); file.Write(36 + data); file.Write("WAVE"u8);
            file.Write("fmt "u8); file.Write(16); file.Write((short)1); file.Write((short)channels);
            file.Write(rate); file.Write(rate * channels * 2); file.Write((short)(channels * 2)); file.Write((short)16);
            file.Write("data"u8); file.Write(data);
            for (var i = 0; i < rate; i++)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * short.MaxValue / 2);
                file.Write(sample);
                file.Write(sample);
            }
        }

        var decoder = new MediaFoundationDecoder();
        var info = decoder.Probe(path);
        Assert.NotNull(info);
        Assert.False(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal(channels, info.Channels);
        Assert.InRange(info.Duration.TotalSeconds, 0.95, 1.05);

        using var sound = decoder.OpenAudio(path);
        Assert.NotNull(sound);
        var buffer = new float[4096];
        var (read, loudest) = (0, 0f);
        for (int n; (n = sound.Read(buffer, 0, buffer.Length)) > 0; read += n)
            loudest = Math.Max(loudest, buffer.Take(n).Max(Math.Abs));
        Assert.InRange(read / (double)(sound.SampleRate * sound.Channels), 0.95, 1.05);
        Assert.InRange(loudest, 0.45f, 0.55f);

        Assert.Null(decoder.Probe(Path.Combine(_dir, "missing.wav")));
    }
}
#endif
