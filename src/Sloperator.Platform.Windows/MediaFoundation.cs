using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using static Sloperator.Platform.Windows.ComAbi;

namespace Sloperator.Platform.Windows;

/// <summary>
/// Video frames and sound through Windows' own Media Foundation (its Source Reader): MP4/H.264,
/// MOV, WMV, MP3, AAC, FLAC and WAV play on every Windows 10 and 11 without anything installed.
/// The COM interfaces are called through their vtables (<see cref="ComAbi"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationDecoder : IMediaDecoder
{
    public MediaInfo? Probe(string path)
    {
        using var video = TryOpen(path, video: true, maxSide: 0);
        using var audio = TryOpen(path, video: false, maxSide: 0);
        if (video is null && audio is null)
            return null;
        return new MediaInfo
        {
            Duration = (video ?? audio)!.Duration,
            HasVideo = video is not null,
            Width = video?.NativeWidth ?? 0,
            Height = video?.NativeHeight ?? 0,
            FrameRate = video?.FrameRate ?? 0,
            HasAudio = audio is not null,
            SampleRate = audio?.SampleRate ?? 0,
            Channels = audio?.Channels ?? 0,
        };
    }

    public IVideoReader? OpenVideo(string path, int maxSide) => TryOpen(path, video: true, maxSide);

    public IAudioReader? OpenAudio(string path) => TryOpen(path, video: false, 0);

    private static Reader? TryOpen(string path, bool video, int maxSide)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return new Reader(path, video, maxSide);
        }
        catch (COMException)
        {
            return null;
        }
    }

    // ---- Media Foundation ------------------------------------------------------------------

    private const uint MF_VERSION = 0x00020070;
    private const uint FirstVideoStream = 0xFFFFFFFC, FirstAudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    private const uint EndOfStream = 0x2, StreamError = 0x1;

    private static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid VideoType = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatAudio = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid Mp3Audio = new("00000055-0000-0010-8000-00AA00389B71");
    private static readonly Guid MpegAudio = new("00000050-0000-0010-8000-00AA00389B71");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid AudioRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid AudioBits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out nint attributes, uint size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);

    private static readonly Lazy<bool> Started = new(() => MFStartup(MF_VERSION, 0) >= 0);

    // IMFAttributes: GetUINT32 7, GetUINT64 8, GetGUID 10, SetUINT32 21, SetUINT64 22, SetGUID 24.
    private static Guid GetGuid(nint attrs, Guid key)
    {
        Guid v;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attrs, 10))(attrs, &key, &v) >= 0 ? v : Guid.Empty;
    }

    private static int GetUInt32(nint attrs, Guid key, out uint value)
    {
        uint v;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(attrs, 7))(attrs, &key, &v);
        value = v;
        return hr;
    }

    private static int GetUInt64(nint attrs, Guid key, out ulong value)
    {
        ulong v;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(attrs, 8))(attrs, &key, &v);
        value = v;
        return hr;
    }

    private static int SetUInt32(nint attrs, Guid key, uint value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attrs, 21))(attrs, &key, value);
    private static int SetUInt64(nint attrs, Guid key, ulong value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attrs, 22))(attrs, &key, value);
    private static int SetGuid(nint attrs, Guid key, Guid value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attrs, 24))(attrs, &key, &value);

    /// <summary>One file opened for its video or its sound.</summary>
    private sealed class Reader : IVideoReader, IAudioReader
    {
        private nint _reader;
        private readonly bool _video;
        private readonly uint _stream;
        private readonly int _stride;
        private TimeSpan _skipBefore = TimeSpan.MinValue;
        /// <summary>The time a sound was sought to, until the sample holding it has been read: what is before it in that sample is left out.</summary>
        private TimeSpan? _soundFrom;
        /// <summary>
        /// An MP3 (or MP2): Media Foundation seeks it by guesswork, and its times after a seek were up to
        /// half a second off the sound read from the start (the waveform), so it is read from the start.
        /// </summary>
        private readonly bool _roughSeeks;
        /// <summary>How many values (samples × channels) are still to be read past, reading from the start to a time.</summary>
        private long _drop;
        private float[] _left = [];
        private int _leftAt;

        public int Width { get; }
        public int Height { get; }
        public int NativeWidth { get; }
        public int NativeHeight { get; }
        public double FrameRate { get; }
        public TimeSpan Duration { get; }
        public int SampleRate { get; }
        public int Channels { get; }

        public Reader(string path, bool video, int maxSide)
        {
            Start();
            if (!Started.Value)
                throw new COMException("Media Foundation is not available (an N edition of Windows without its Media Feature Pack?).");
            _video = video;
            _stream = video ? FirstVideoStream : FirstAudioStream;
            Check(MFCreateAttributes(out var attrs, 1));
            try
            {
                if (video)
                    Check(SetUInt32(attrs, AdvancedVideoProcessing, 1));
                Check(MFCreateSourceReaderFromURL(path, attrs, out _reader));
            }
            finally
            {
                Release(attrs);
            }
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_reader, 4))(_reader, AllStreams, 0));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_reader, 4))(_reader, _stream, 1));

                PropVariant duration;
                var durationKey = DurationKey;
                if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, PropVariant*, int>)Slot(_reader, 12))(_reader, MediaSource, &durationKey, &duration) >= 0)
                    Duration = TimeSpan.FromTicks(duration.Value);

                if (video)
                {
                    nint native;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(_reader, 5))(_reader, _stream, 0, &native));
                    try
                    {
                        if (GetUInt64(native, FrameSize, out var size) >= 0)
                            (NativeWidth, NativeHeight) = ((int)(size >> 32), (int)(size & 0xFFFFFFFF));
                        if (GetUInt64(native, FrameRateKey, out var rate) >= 0 && (rate & 0xFFFFFFFF) != 0)
                            FrameRate = (double)(rate >> 32) / (rate & 0xFFFFFFFF);
                    }
                    finally
                    {
                        Release(native);
                    }
                    var (w, h) = (NativeWidth, NativeHeight);
                    if (maxSide > 0 && w > 0 && h > 0 && Math.Max(w, h) > maxSide)
                    {
                        var scale = (double)maxSide / Math.Max(w, h);
                        (w, h) = (Math.Max(2, (int)Math.Round(w * scale / 2) * 2), Math.Max(2, (int)Math.Round(h * scale / 2) * 2));
                    }
                    // Scaled by the video processor when it can, else at the video's own size.
                    if (!SetOutput(VideoType, Rgb32, (w, h)) && !SetOutput(VideoType, Rgb32, null))
                        throw new COMException("The video can't be decoded to pictures.");
                    nint current;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_reader, 6))(_reader, _stream, &current));
                    try
                    {
                        Check(GetUInt64(current, FrameSize, out var outSize));
                        (Width, Height) = ((int)(outSize >> 32), (int)(outSize & 0xFFFFFFFF));
                        _stride = GetUInt32(current, DefaultStride, out var stride) >= 0 ? (int)stride : Width * 4;
                    }
                    finally
                    {
                        Release(current);
                    }
                }
                else
                {
                    nint native;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(_reader, 5))(_reader, _stream, 0, &native));
                    try
                    {
                        var coded = GetGuid(native, SubType);
                        _roughSeeks = coded == Mp3Audio || coded == MpegAudio;
                    }
                    finally
                    {
                        Release(native);
                    }
                    if (!SetOutput(AudioType, FloatAudio, null))
                        throw new COMException("The sound can't be decoded.");
                    nint current;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_reader, 6))(_reader, _stream, &current));
                    try
                    {
                        Check(GetUInt32(current, AudioRate, out var rate));
                        Check(GetUInt32(current, AudioChannels, out var channels));
                        if (GetUInt32(current, AudioBits, out var bits) >= 0 && bits != 32)
                            throw new COMException("The sound didn't decode to 32-bit floats.");
                        (SampleRate, Channels) = ((int)rate, (int)channels);
                    }
                    finally
                    {
                        Release(current);
                    }
                }
            }
            catch
            {
                Release(_reader);
                _reader = 0;
                throw;
            }
        }

        private bool SetOutput(Guid major, Guid sub, (int W, int H)? size)
        {
            Check(MFCreateMediaType(out var type));
            try
            {
                Check(SetGuid(type, MajorType, major));
                Check(SetGuid(type, SubType, sub));
                if (size is { } s)
                    Check(SetUInt64(type, FrameSize, ((ulong)s.W << 32) | (uint)s.H));
                return ((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Slot(_reader, 7))(_reader, _stream, null, type) >= 0;
            }
            finally
            {
                Release(type);
            }
        }

        /// <summary>The next sample's bytes and time; null at the end of the stream.</summary>
        private (byte[] Bytes, TimeSpan Time)? ReadSample()
        {
            while (true)
            {
                uint actual, flags;
                long time;
                nint sample = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(_reader, 9))(_reader, _stream, 0, &actual, &flags, &time, &sample));
                try
                {
                    if ((flags & StreamError) != 0)
                        throw new COMException("The file couldn't be decoded further.");
                    if (sample == 0)
                    {
                        if ((flags & EndOfStream) != 0) return null;
                        continue; // a gap in the stream
                    }
                    nint buffer;
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer));
                    try
                    {
                        byte* data;
                        uint max, length;
                        Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length));
                        try
                        {
                            var bytes = new byte[length];
                            Marshal.Copy((nint)data, bytes, 0, (int)length);
                            return (bytes, TimeSpan.FromTicks(time));
                        }
                        finally
                        {
                            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
                        }
                    }
                    finally
                    {
                        Release(buffer);
                    }
                }
                finally
                {
                    Release(sample);
                }
            }
        }

        public (TimeSpan Time, RawImage Frame)? Next()
        {
            while (ReadSample() is { } s)
            {
                // After a seek the reader starts at the key frame before the time asked for.
                if (s.Time < _skipBefore)
                    continue;
                _skipBefore = TimeSpan.MinValue;
                var rowBytes = Width * 4;
                var bgra = new byte[rowBytes * Height];
                var stride = Math.Abs(_stride) >= rowBytes ? _stride : rowBytes;
                for (var y = 0; y < Height; y++)
                {
                    var from = stride > 0 ? y * stride : (Height - 1 - y) * -stride;
                    if (from + rowBytes > s.Bytes.Length) break;
                    Buffer.BlockCopy(s.Bytes, from, bgra, y * rowBytes, rowBytes);
                }
                for (var i = 3; i < bgra.Length; i += 4)
                    bgra[i] = 255; // RGB32 leaves the fourth byte undefined
                return (s.Time, new RawImage(Width, Height, bgra));
            }
            return null;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (_leftAt >= _left.Length)
                {
                    if (ReadSample() is not { } s)
                        break;
                    // After a seek the reader starts somewhere before the time asked for (a FLAC
                    // block, an MP3 frame or more): read from that time's own sample on. Skipping
                    // whole samples that began before it started an MP3 up to half a second late.
                    var from = 0;
                    if (_drop > 0)
                    {
                        if (_drop >= s.Bytes.Length / 4)
                        {
                            _drop -= s.Bytes.Length / 4;
                            continue;
                        }
                        from = (int)_drop;
                        _drop = 0;
                    }
                    else if (_soundFrom is { } at)
                    {
                        var channels = Math.Max(1, Channels);
                        var frames = s.Bytes.Length / 4 / channels;
                        var skip = (long)Math.Round((at - s.Time).TotalSeconds * SampleRate);
                        if (skip >= frames)
                            continue;
                        _soundFrom = null;
                        from = (int)Math.Max(0, skip) * channels;
                    }
                    _left = new float[s.Bytes.Length / 4 - from];
                    Buffer.BlockCopy(s.Bytes, from * 4, _left, 0, _left.Length * 4);
                    _leftAt = 0;
                }
                var n = Math.Min(count - written, _left.Length - _leftAt);
                Array.Copy(_left, _leftAt, buffer, offset + written, n);
                _leftAt += n;
                written += n;
            }
            return written;
        }

        public void Seek(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            // Read through from the start, which takes some 130 ms a minute of MP3 on a laptop.
            var rough = !_video && _roughSeeks;
            _drop = rough ? (long)Math.Round(time.TotalSeconds * SampleRate) * Math.Max(1, Channels) : 0;
            var position = new PropVariant { Type = 20 /* VT_I8 */, Value = rough ? 0 : time.Ticks };
            var format = Guid.Empty;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(_reader, 8))(_reader, &format, &position));
            // Frames from the key frame before it up to half a frame early are skipped.
            _skipBefore = _video && FrameRate > 0 ? time - TimeSpan.FromSeconds(0.5 / FrameRate) : TimeSpan.MinValue;
            _soundFrom = _video || rough ? null : time;
            _left = [];
            _leftAt = 0;
        }

        public void Dispose()
        {
            Release(_reader);
            _reader = 0;
        }
    }
}
