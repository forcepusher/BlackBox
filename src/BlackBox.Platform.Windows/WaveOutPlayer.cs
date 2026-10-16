using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Media;

namespace BlackBox.Platform.Windows;

/// <summary>
/// Plays a sound through Windows' waveOut API (the default speakers, at the sound's own rate; the
/// system mixes and resamples), fed from a decoder on a thread of its own, for the chat's players.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WaveOutPlayer : IAudioOutput
{
    private const int BufferCount = 4;
    private const double BufferSeconds = 0.12;

    private nint _device;
    private IAudioReader? _source;
    private Thread? _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stopping;
    private volatile bool _ended;
    private int _rate = 44100, _channels = 2;
    private readonly nint[] _headers = new nint[BufferCount];
    private readonly nint[] _data = new nint[BufferCount];

    public bool Ended => _ended;

    public void Play(IAudioReader source)
    {
        if (_source is not null)
            throw new InvalidOperationException("This output is playing already; make a new one.");
        _source = source;
        _rate = source.SampleRate;
        _channels = Math.Clamp(source.Channels, 1, 2);
        var format = new WaveFormat
        {
            FormatTag = 1, // PCM
            Channels = (ushort)_channels,
            SamplesPerSec = (uint)_rate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(_channels * 2),
            AvgBytesPerSec = (uint)(_rate * _channels * 2),
        };
        Check(waveOutOpen(out _device, WaveMapper, ref format, _wake.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent), "open the speakers");
        var bytes = (int)(_rate * BufferSeconds) * _channels * 2;
        for (var i = 0; i < BufferCount; i++)
        {
            _data[i] = Marshal.AllocHGlobal(bytes);
            _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = (uint)bytes, Flags = HeaderDone }, _headers[i], false);
        }
        _thread = new Thread(Pump) { IsBackground = true, Name = "BlackBox audio" };
        _thread.Start();
    }

    private void Pump()
    {
        try
        {
            PumpBuffers();
        }
        catch (Exception)
        {
            // A sound that stops decoding midway ends there; it must not take BlackBox down.
            _ended = true;
        }
    }

    private void PumpBuffers()
    {
        var samples = new float[(int)(_rate * BufferSeconds) * Math.Max(2, _source!.Channels)];
        var sourceChannels = Math.Max(1, _source.Channels);
        var finished = false;
        var prepared = new bool[BufferCount];
        while (!_stopping)
        {
            var anyQueued = false;
            for (var i = 0; i < BufferCount && !_stopping; i++)
            {
                var header = Marshal.PtrToStructure<WaveHeader>(_headers[i]);
                if ((header.Flags & HeaderDone) == 0)
                {
                    anyQueued = true;
                    continue;
                }
                if (finished)
                    continue;
                if (prepared[i])
                {
                    waveOutUnprepareHeader(_device, _headers[i], (uint)Marshal.SizeOf<WaveHeader>());
                    prepared[i] = false;
                }
                // Fill it: the sound's own channels down to one or two, as 16-bit samples.
                var frames = (int)(_rate * BufferSeconds);
                var read = _source.Read(samples, 0, frames * sourceChannels);
                var got = read / sourceChannels;
                if (got <= 0)
                {
                    finished = true;
                    continue;
                }
                var pcm = new short[got * _channels];
                for (var f = 0; f < got; f++)
                {
                    if (_channels == 1 || sourceChannels == 1)
                    {
                        var s = sourceChannels == 1 ? samples[f] : (samples[f * sourceChannels] + samples[f * sourceChannels + 1]) / 2;
                        for (var c = 0; c < _channels; c++) pcm[f * _channels + c] = ToShort(s);
                    }
                    else
                    {
                        pcm[f * 2] = ToShort(samples[f * sourceChannels]);
                        pcm[f * 2 + 1] = ToShort(samples[f * sourceChannels + 1]);
                    }
                }
                Marshal.Copy(pcm, 0, _data[i], pcm.Length);
                Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = (uint)(pcm.Length * 2) }, _headers[i], false);
                waveOutPrepareHeader(_device, _headers[i], (uint)Marshal.SizeOf<WaveHeader>());
                prepared[i] = true;
                waveOutWrite(_device, _headers[i], (uint)Marshal.SizeOf<WaveHeader>());
                anyQueued = true;
            }
            if (finished && !anyQueued)
            {
                _ended = true;
                return;
            }
            _wake.WaitOne(50);
        }
    }

    private static short ToShort(float s) => (short)Math.Clamp(MathF.Round(s * 32767f), -32768, 32767);

    public void Pause()
    {
        if (_device != 0) waveOutPause(_device);
    }

    public void Resume()
    {
        if (_device != 0) waveOutRestart(_device);
    }

    public TimeSpan Played
    {
        get
        {
            if (_device == 0) return TimeSpan.Zero;
            var time = new MmTime { Type = TimeSamples };
            if (waveOutGetPosition(_device, ref time, (uint)Marshal.SizeOf<MmTime>()) != 0 || time.Type != TimeSamples)
                return TimeSpan.Zero;
            return TimeSpan.FromSeconds(time.Value / (double)_rate);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(2000);
        if (_device != 0)
        {
            waveOutReset(_device);
            for (var i = 0; i < BufferCount; i++)
                if (_headers[i] != 0)
                    waveOutUnprepareHeader(_device, _headers[i], (uint)Marshal.SizeOf<WaveHeader>());
            waveOutClose(_device);
            _device = 0;
        }
        for (var i = 0; i < BufferCount; i++)
        {
            if (_headers[i] != 0) Marshal.FreeHGlobal(_headers[i]);
            if (_data[i] != 0) Marshal.FreeHGlobal(_data[i]);
            _headers[i] = _data[i] = 0;
        }
        _source?.Dispose();
        _source = null;
        _wake.Dispose();
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
            throw new InvalidOperationException($"Windows couldn't {what} (waveOut error {result}).");
    }

    private const uint WaveMapper = 0xFFFFFFFF, CallbackEvent = 0x00050000, HeaderDone = 1, TimeSamples = 2;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out nint device, uint deviceId, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(nint device);
    [DllImport("winmm.dll")] private static extern int waveOutPause(nint device);
    [DllImport("winmm.dll")] private static extern int waveOutRestart(nint device);
    [DllImport("winmm.dll")] private static extern int waveOutClose(nint device);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(nint device, ref MmTime time, uint size);
}
