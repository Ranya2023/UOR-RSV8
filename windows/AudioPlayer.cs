using NAudio.Wave;

namespace Remco;

/// <summary>Plays the phone's sound (raw 16-bit PCM chunks) on the PC speakers / projector.</summary>
internal sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _out;
    private BufferedWaveProvider? _buf;
    private int _rate, _ch;

    public void Add(byte[] pcm, int rate, int channels)
    {
        if (_out == null || rate != _rate || channels != _ch)
        {
            Stop();
            _rate = rate; _ch = channels;
            _buf = new BufferedWaveProvider(new WaveFormat(rate, 16, channels))
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true
            };
            _out = new WaveOutEvent { DesiredLatency = 160, NumberOfBuffers = 3 };
            _out.Init(_buf);
            _out.Play();
        }
        // keep the sound in step with the picture: if we fall behind, skip ahead
        if (_buf!.BufferedDuration > TimeSpan.FromMilliseconds(450)) _buf.ClearBuffer();
        _buf.AddSamples(pcm, 0, pcm.Length);
    }

    public void Stop()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null; _buf = null;
    }

    public void Dispose() => Stop();
}
