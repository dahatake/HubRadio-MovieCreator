using System.Buffers.Binary;
using HubRadio.Core.Identity;

namespace HubRadio.App.Rendering;

/// <summary>
/// FR-060: the two sound effects are synthesized by this code (sine tones with a short envelope), so
/// they carry no third-party rights. The bytes are deterministic; their SHA-256 is pinned by tests.
/// </summary>
public static class StandardSoundEffectSynthesizer
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    /// <summary>Mixed this many dB below the original level (before final loudness normalization).</summary>
    public const int MixGainDecibels = -14;

    public static string FileName(StandardSoundEffectKind kind) => kind switch
    {
        StandardSoundEffectKind.Transition => "fr060-transition-v1.wav",
        StandardSoundEffectKind.Emphasis => "fr060-emphasis-v1.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static byte[] Create(StandardSoundEffectKind kind)
    {
        int frames = SampleRate * 3 / 10;
        var samples = new short[frames];
        for (int index = 0; index < frames; index++)
        {
            double time = (double)index / SampleRate;
            double attack = Math.Min(1.0, time / 0.005);
            double value = kind switch
            {
                // A soft rising sweep (600 → 1200 Hz) that fades out: a light "whoosh" for a new topic.
                StandardSoundEffectKind.Transition => attack * Math.Exp(-time / 0.12)
                    * Math.Sin(2 * Math.PI * (600 * time + 1000 * time * time)),
                // A bright two-tone "pop" with a fast decay for an emphasis caption.
                StandardSoundEffectKind.Emphasis => attack * Math.Exp(-time / 0.06)
                    * (0.6 * Math.Sin(2 * Math.PI * 1320 * time) + 0.4 * Math.Sin(2 * Math.PI * 1760 * time)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            samples[index] = (short)Math.Round(value * 0.5 * short.MaxValue, MidpointRounding.ToEven);
        }

        int dataBytes = frames * Channels * sizeof(short);
        byte[] wav = new byte[44 + dataBytes];
        Span<byte> span = wav;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * Channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], Channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);
        for (int index = 0; index < frames; index++)
        {
            for (int channel = 0; channel < Channels; channel++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(span[(44 + ((index * Channels) + channel) * sizeof(short))..], samples[index]);
            }
        }

        return wav;
    }

    public static ContentDigest Digest(StandardSoundEffectKind kind) => ContentDigest.Compute(Create(kind));
}
