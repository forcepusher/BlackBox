using BlackBox.Core.Platform;

namespace BlackBox.Core.Media;

/// <summary>What a video or sound file holds.</summary>
public sealed record MediaInfo
{
    public TimeSpan Duration { get; init; }
    public bool HasVideo { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public bool HasAudio { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }

    /// <summary>"video 640x640, 81 frames at 16 fps (5.1 s), with sound" or "sound 12.0 s, 44.1 kHz stereo".</summary>
    public string Describe()
    {
        var seconds = $"{Duration.TotalSeconds:0.0} s";
        if (HasVideo)
        {
            var frames = FrameRate > 0 ? $"{Math.Round(Duration.TotalSeconds * FrameRate):0} frames at {FrameRate:0.##} fps ({seconds})" : seconds;
            return $"video {Width}x{Height}, {frames}, " + (HasAudio ? $"with sound ({Rate()})" : "no sound");
        }
        return HasAudio ? $"sound {seconds}, {Rate()}" : $"media {seconds}";

        string Rate() => $"{SampleRate / 1000.0:0.#} kHz {(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : Channels + " channels")}";
    }
}

/// <summary>A video's frames one after another, scaled as asked when it was opened.</summary>
public interface IVideoReader : IDisposable
{
    int Width { get; }
    int Height { get; }
    TimeSpan Duration { get; }
    double FrameRate { get; }

    /// <summary>The next frame and when it shows; null at the end.</summary>
    (TimeSpan Time, RawImage Frame)? Next();

    /// <summary>Goes to <paramref name="time"/>; the next frame is the one showing then (or the first after it).</summary>
    void Seek(TimeSpan time);
}

/// <summary>A sound as interleaved 32-bit float samples, at its own rate and channel count.</summary>
public interface IAudioReader : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    TimeSpan Duration { get; }

    /// <summary>Fills <paramref name="buffer"/> with up to <paramref name="count"/> samples (all channels counted); 0 at the end.</summary>
    int Read(float[] buffer, int offset, int count);

    void Seek(TimeSpan time);
}

/// <summary>Reads video and sound files: the OS's own decoders where there are some, ffmpeg otherwise.</summary>
public interface IMediaDecoder
{
    /// <summary>What the file holds; null when it can't be read.</summary>
    MediaInfo? Probe(string path);

    /// <summary>Its video, scaled to fit <paramref name="maxSide"/> (0: as it is); null when it has none or can't be read.</summary>
    IVideoReader? OpenVideo(string path, int maxSide);

    /// <summary>Its sound; null when it has none or can't be read.</summary>
    IAudioReader? OpenAudio(string path);

    /// <summary>The ffmpeg program a decoder reads with, when it uses one: also the agent's, to cut or join media.</summary>
    string? Ffmpeg => null;
}

/// <summary>Plays a sound through the PC's speakers, for the chat's audio and video players.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Plays <paramref name="source"/> from where it stands until it ends or is stopped; the source belongs to the output from then on.</summary>
    void Play(IAudioReader source);

    void Pause();
    void Resume();

    /// <summary>How far the sound has played, counted from where the source stood at <see cref="Play"/>.</summary>
    TimeSpan Played { get; }

    /// <summary>Played everything it was given.</summary>
    bool Ended { get; }
}

/// <summary>One decoder tried after another: the OS's first, ffmpeg for what it can't read.</summary>
public sealed class FallbackDecoder(params IMediaDecoder[] decoders) : IMediaDecoder
{
    public MediaInfo? Probe(string path) => decoders.Select(d => Try(() => d.Probe(path))).FirstOrDefault(i => i is not null);

    public IVideoReader? OpenVideo(string path, int maxSide) => decoders.Select(d => Try(() => d.OpenVideo(path, maxSide))).FirstOrDefault(r => r is not null);

    public IAudioReader? OpenAudio(string path) => decoders.Select(d => Try(() => d.OpenAudio(path))).FirstOrDefault(r => r is not null);

    public string? Ffmpeg => decoders.Select(d => d.Ffmpeg).FirstOrDefault(f => f is not null);

    private static T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or FormatException)
        {
            return null;
        }
    }
}
