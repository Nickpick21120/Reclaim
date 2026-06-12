using System.IO;
using System.Media;

namespace Reclaim.App;

/// <summary>
/// Fire-and-forget 8-bit sound effects, synthesized in memory. Each call plays
/// on its own short-lived SoundPlayer so effects can overlap the background
/// music. Purely cosmetic; failures are swallowed so audio never breaks a game.
/// </summary>
public static class SoundFx
{
    private const int SampleRate = 22050;
    private static byte[]? _blastCache;

    private static double _volume = 0.30;
    public static double Volume
    {
        get => _volume;
        set { _volume = value; _blastCache = null; } // rebuild at new volume
    }

    /// <summary>A short descending "pew" laser blast.</summary>
    public static void Blast()
    {
        try
        {
            _blastCache ??= BuildBlast();
            var player = new SoundPlayer(new MemoryStream(_blastCache));
            player.Play(); // async; returns immediately
        }
        catch
        {
            // Non-essential.
        }
    }

    private static byte[] BuildBlast()
    {
        // ~0.12s, frequency sweeping downward — classic arcade laser.
        var samples = new List<short>();
        const double seconds = 0.12;
        var total = (int)(SampleRate * seconds);
        const double startFreq = 1200, endFreq = 300;
        var amp = 0.5 * Volume;

        double phase = 0;
        for (var i = 0; i < total; i++)
        {
            var t = i / (double)total;
            var freq = startFreq + (endFreq - startFreq) * t;
            phase += 2 * Math.PI * freq / SampleRate;
            var square = Math.Sin(phase) >= 0 ? 1.0 : -1.0;
            var env = 1.0 - t; // quick fade out
            samples.Add((short)(square * amp * env * short.MaxValue));
        }

        return WrapWav(samples);
    }

    private static byte[] WrapWav(List<short> samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Count * 2;
        const short channels = 1, bitsPerSample = 16;
        var byteRate = SampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
