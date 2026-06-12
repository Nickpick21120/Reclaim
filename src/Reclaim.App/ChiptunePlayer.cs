using System.IO;
using System.Media;

namespace Reclaim.App;

/// <summary>
/// A tiny procedural chiptune player for the Junk Popper game. It synthesizes a
/// looping square-wave melody into an in-memory WAV and plays it — no audio
/// files shipped, no dependencies. Square waves give the classic 8-bit sound.
/// </summary>
public sealed class ChiptunePlayer : IDisposable
{
    private readonly SoundPlayer _player = new();
    private bool _playing;

    private const int SampleRate = 22050;
    /// <summary>Amplitude at 100% volume. Actual lead amplitude = BaseAmplitude * Volume.</summary>
    private const double BaseAmplitude = 0.18;

    private double _volume = 0.30; // 30% default

    /// <summary>Playback volume in [0,1]. Setting it while playing rebuilds the
    /// loop at the new level (SoundPlayer has no live volume control).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _volume) < 0.001)
                return;
            _volume = v;
            if (_playing)
                Restart();
        }
    }

    // A longer original loop in A minor — a bit of an arc rather than a short
    // jingle. Lead voice (melody). Each entry is (frequency Hz, beats); 0 = rest.
    private static readonly (double Freq, double Beats)[] Lead =
    [
        // Phrase A — stating the theme
        (A4, 1), (C5, 1), (E5, 1), (A5, 1), (G5, 0.5), (E5, 0.5), (C5, 1),
        (D5, 1), (F5, 1), (A5, 1), (G5, 1), (E5, 1), (D5, 1), (C5, 1),
        // Phrase B — lift
        (E5, 1), (E5, 0.5), (F5, 0.5), (G5, 1), (A5, 1), (B5, 1), (C6, 2), (0, 1),
        (G5, 1), (E5, 1), (C5, 1), (D5, 1), (E5, 2), (0, 1),
        // Phrase C — descent / question
        (A5, 1), (G5, 1), (F5, 1), (E5, 1), (D5, 1), (C5, 1), (B4, 1), (A4, 1),
        (A4, 0.5), (B4, 0.5), (C5, 0.5), (D5, 0.5), (E5, 1), (A4, 1),
        // Phrase D — resolve
        (C5, 1), (E5, 1), (D5, 1), (B4, 1), (C5, 1), (A4, 1), (A4, 2), (0, 2),
    ];

    // Bass voice — slower, supports the lead. Totals 50 beats to match the lead.
    private static readonly (double Freq, double Beats)[] Bass =
    [
        (A2, 2), (A2, 2), (F2, 2),                          // A1 = 6
        (G2, 2), (G2, 2), (C3, 2), (C3, 1),                 // A2 = 7
        (C3, 2), (C3, 2), (G2, 2), (G2, 2),                 // B1 = 8
        (C3, 2), (A2, 2), (E3, 2), (E3, 1),                 // B2 = 7
        (F2, 2), (F2, 2), (D3, 2), (E3, 2),                 // C1 = 8
        (F2, 2), (E3, 2),                                   // C2 = 4
        (F2, 2), (G2, 2), (C3, 2), (A2, 2), (A2, 2),        // D  = 10
    ];

    // Note frequencies (Hz).
    private const double A2 = 110.00, C3 = 130.81, D3 = 146.83, E3 = 164.81, F2 = 87.31, G2 = 98.00;
    private const double A4 = 440.00, B4 = 493.88;
    private const double C5 = 523.25, D5 = 587.33, E5 = 659.25, F5 = 698.46, G5 = 783.99, A5 = 880.00, B5 = 987.77;
    private const double C6 = 1046.50;

    public void Start()
    {
        if (_playing)
            return;
        try
        {
            var wav = BuildLoopWav();
            _player.Stream = new MemoryStream(wav);
            _player.PlayLooping();
            _playing = true;
        }
        catch
        {
            // Audio is non-essential; never let it break the game.
            _playing = false;
        }
    }

    public void Stop()
    {
        try { _player.Stop(); } catch { /* ignore */ }
        _playing = false;
    }

    private void Restart()
    {
        try
        {
            var wav = BuildLoopWav();
            _player.Stream = new MemoryStream(wav);
            _player.PlayLooping();
            _playing = true;
        }
        catch
        {
            _playing = false;
        }
    }

    private byte[] BuildLoopWav()
    {
        const double bpm = 200;
        var secondsPerBeat = 60.0 / bpm;

        var leadAmp = BaseAmplitude * _volume;
        var bassAmp = BaseAmplitude * _volume * 0.55; // bass a touch quieter

        var lead = RenderVoice(Lead, secondsPerBeat, leadAmp);
        var bass = RenderVoice(Bass, secondsPerBeat, bassAmp);

        // Mix the two voices sample-for-sample (lengths match by construction,
        // but guard with the longer length just in case of rounding).
        var n = Math.Max(lead.Count, bass.Count);
        var mixed = new List<short>(n);
        for (var i = 0; i < n; i++)
        {
            var a = i < lead.Count ? lead[i] : 0;
            var b = i < bass.Count ? bass[i] : 0;
            var sum = a + b;
            // Clamp to 16-bit range to avoid wrap-around distortion.
            if (sum > short.MaxValue) sum = short.MaxValue;
            else if (sum < short.MinValue) sum = short.MinValue;
            mixed.Add((short)sum);
        }

        return WrapWav(mixed);
    }

    private static List<short> RenderVoice(
        (double Freq, double Beats)[] voice, double secondsPerBeat, double amplitude)
    {
        var samples = new List<short>();
        foreach (var (freq, beats) in voice)
        {
            var count = (int)(SampleRate * secondsPerBeat * beats);
            AppendNote(samples, freq, count, amplitude);
        }
        return samples;
    }

    /// <summary>Appends one note as a square wave, with a short fade at the end
    /// to avoid clicks between notes.</summary>
    private static void AppendNote(List<short> buffer, double freq, int count, double amplitude)
    {
        if (freq <= 0) // rest
        {
            for (var i = 0; i < count; i++)
                buffer.Add(0);
            return;
        }

        var period = SampleRate / freq;
        var fade = Math.Min(400, count / 4);
        for (var i = 0; i < count; i++)
        {
            var phase = i % period;
            var square = phase < period / 2 ? 1.0 : -1.0;

            var env = 1.0;
            if (i > count - fade)
                env = (count - i) / (double)fade;

            var sample = square * amplitude * env;
            buffer.Add((short)(sample * short.MaxValue));
        }
    }

    /// <summary>Wraps 16-bit mono PCM samples in a minimal WAV container.</summary>
    private static byte[] WrapWav(List<short> samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        var dataBytes = samples.Count * 2;
        const short channels = 1;
        const short bitsPerSample = 16;
        var byteRate = SampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        // RIFF header
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        // fmt chunk
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write(channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);
        // data chunk
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples)
            w.Write(s);

        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
    }
}
