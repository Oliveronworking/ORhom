using NAudio.Wave;

namespace ORhom.Tests;

public sealed class AudioLevelMeterTests
{
    [Fact]
    public void Pcm16PeakUsesEveryChannelAndNormalizesTheFullRange()
    {
        short[] samples = [0, -16_384, 4_096, short.MaxValue];
        var bytes = samples
            .SelectMany(BitConverter.GetBytes)
            .ToArray();

        var peak = AudioLevelMeter.MeasurePeak(
            bytes,
            new WaveFormat(16_000, 16, 2));

        Assert.InRange(peak, 0.999f, 1f);
    }

    [Fact]
    public void FloatPeakIgnoresNonFiniteSamplesAndClampsOverload()
    {
        float[] samples =
        [
            float.NaN,
            float.PositiveInfinity,
            -1.25f,
            0.25f
        ];
        var bytes = samples
            .SelectMany(BitConverter.GetBytes)
            .ToArray();

        var peak = AudioLevelMeter.MeasurePeak(
            bytes,
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));

        Assert.Equal(1f, peak);
    }

    [Fact]
    public void Pcm24HandlesNegativeFullScaleAndIncompleteFrames()
    {
        byte[] bytes =
        [
            0x00, 0x00, 0x80,
            0xFF, 0xFF, 0x7F,
            0x7A
        ];

        var peak = AudioLevelMeter.MeasurePeak(
            bytes,
            new WaveFormat(16_000, 24, 1));

        Assert.Equal(1f, peak);
    }

    [Fact]
    public void SmoothingAttacksQuicklyAndDecaysGradually()
    {
        var rising = AudioLevelMeter.Smooth(0f, 1f);
        var falling = AudioLevelMeter.Smooth(1f, 0f);

        Assert.InRange(rising, 0.7f, 0.75f);
        Assert.InRange(falling, 0.8f, 0.9f);
        Assert.Equal(0f, AudioLevelMeter.Smooth(float.NaN, float.NaN));
        Assert.InRange(
            AudioLevelMeter.Smooth(2f, -1f),
            0f,
            1f);
    }
}
