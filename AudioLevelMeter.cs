using System.Buffers.Binary;
using NAudio.Wave;

namespace ORhom;

internal static class AudioLevelMeter
{
    private const float AttackFactor = 0.72f;
    private const float DecayFactor = 0.16f;
    private const float SilenceFloor = 0.0005f;

    public static float MeasurePeak(
        ReadOnlySpan<byte> buffer,
        WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (buffer.IsEmpty)
        {
            return 0f;
        }

        var usableFormat = NormalizeFormat(format);
        var bytesPerSample = usableFormat.BitsPerSample / 8;
        if (usableFormat.Channels <= 0 ||
            usableFormat.BlockAlign <= 0 ||
            bytesPerSample <= 0 ||
            usableFormat.BitsPerSample % 8 != 0 ||
            bytesPerSample * usableFormat.Channels > usableFormat.BlockAlign)
        {
            return 0f;
        }

        var completeLength =
            buffer.Length - buffer.Length % usableFormat.BlockAlign;
        double peak = 0;
        for (var frameOffset = 0;
             frameOffset < completeLength;
             frameOffset += usableFormat.BlockAlign)
        {
            for (var channel = 0;
                 channel < usableFormat.Channels;
                 channel++)
            {
                var sampleOffset = frameOffset + channel * bytesPerSample;
                var sample = DecodeNormalizedSample(
                    buffer.Slice(sampleOffset, bytesPerSample),
                    usableFormat.Encoding,
                    usableFormat.BitsPerSample);
                if (sample > peak)
                {
                    peak = sample;
                }
            }
        }

        return NormalizeLevel((float)peak);
    }

    public static float Smooth(float currentLevel, float measuredLevel)
    {
        var current = NormalizeLevel(currentLevel);
        var measured = NormalizeLevel(measuredLevel);
        var factor = measured >= current
            ? AttackFactor
            : DecayFactor;
        var smoothed = current + (measured - current) * factor;
        return smoothed < SilenceFloor
            ? 0f
            : NormalizeLevel(smoothed);
    }

    public static float NormalizeLevel(float level) =>
        float.IsFinite(level)
            ? Math.Clamp(level, 0f, 1f)
            : 0f;

    private static WaveFormat NormalizeFormat(WaveFormat format) =>
        format is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : format;

    private static double DecodeNormalizedSample(
        ReadOnlySpan<byte> sample,
        WaveFormatEncoding encoding,
        int bitsPerSample)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat)
        {
            var value = bitsPerSample switch
            {
                32 => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(sample)),
                64 => BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64LittleEndian(sample)),
                _ => 0d
            };
            return double.IsFinite(value)
                ? Math.Min(Math.Abs(value), 1d)
                : 0d;
        }

        if (encoding != WaveFormatEncoding.Pcm)
        {
            return 0d;
        }

        return bitsPerSample switch
        {
            8 => Math.Min(Math.Abs((sample[0] - 128) / 128d), 1d),
            16 => Math.Min(
                Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d),
                1d),
            24 => Math.Min(Math.Abs(ReadPcm24(sample) / 8_388_608d), 1d),
            32 => Math.Min(
                Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(sample) /
                         2_147_483_648d),
                1d),
            _ => 0d
        };
    }

    private static int ReadPcm24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] |
                    sample[1] << 8 |
                    sample[2] << 16;
        return (value & 0x0080_0000) != 0
            ? value | unchecked((int)0xFF00_0000)
            : value;
    }
}
