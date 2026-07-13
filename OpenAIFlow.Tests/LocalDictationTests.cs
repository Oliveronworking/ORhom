using NAudio.Wave;

namespace ChatGptDictationBridge.Tests;

public sealed class LocalDictationConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenAIFlow.LocalDictation.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SettingsWithoutProviderMigrateToLocalWhisper()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settingsPath, """
            {
              "toggleHotkey": "F8",
              "preferredMicrophoneName": "Test microphone"
            }
            """);

        var settings = AppSettings.Load(
            settingsPath,
            new AppLogger(Path.Combine(_directory, "logs")));

        Assert.Equal(DictationProviders.LocalWhisper, settings.DictationProvider);
    }

    [Theory]
    [InlineData(null, DictationProviders.LocalWhisper)]
    [InlineData("unknown", DictationProviders.LocalWhisper)]
    [InlineData(DictationProviders.LocalWhisper, DictationProviders.LocalWhisper)]
    [InlineData(DictationProviders.ChatGptBrowser, DictationProviders.ChatGptBrowser)]
    public void ProviderNormalizationIsPrivacyPreserving(
        string? configured,
        string expected)
    {
        Assert.Equal(expected, DictationProviders.Normalize(configured));
    }

    [Fact]
    public void RecognitionLanguageIsAlwaysGerman()
    {
        Assert.Equal("de", LocalWhisperRecognitionService.Language);
    }

    [Theory]
    [InlineData("whisper_backend_init_gpu: using Vulkan0 backend")]
    [InlineData("whisper_model_load: Vulkan0 total size = 1623.92 MB")]
    public void NativeVulkanConfirmationMessagesAreRecognized(string message)
    {
        Assert.True(LocalWhisperRecognitionService.ConfirmsVulkanBackend(message));
    }

    [Fact]
    public void CpuModelLoadDoesNotCountAsVulkanConfirmation()
    {
        Assert.False(LocalWhisperRecognitionService.ConfirmsVulkanBackend(
            "whisper_model_load: CPU total size = 1623.92 MB"));
    }

    [Theory]
    [InlineData("ggml_vulkan: 0 = AMD Radeon RX 7700 XT (AMD proprietary driver)", 0)]
    [InlineData("ggml_vulkan: 2 = AMD Radeon RX 7700 XT | uma: 0", 2)]
    public void TargetGpuIndexIsReadFromNativeVulkanEnumeration(
        string message,
        int expectedIndex)
    {
        Assert.Equal(
            expectedIndex,
            LocalWhisperRecognitionService.FindRx7700XtVulkanDevice(message));
    }

    [Fact]
    public void IntegratedAmdGpuDoesNotMatchTheTargetGpu()
    {
        Assert.Null(LocalWhisperRecognitionService.FindRx7700XtVulkanDevice(
            "ggml_vulkan: 0 = AMD Radeon(TM) Graphics"));
    }

    [Fact]
    public void SilentAudioIsRejectedBeforeInference()
    {
        Assert.False(LocalWhisperRecognitionService.ContainsAudibleSignal(
            new float[16_000]));
    }

    [Fact]
    public void AShortClickIsNotMistakenForSpeechLevelAudio()
    {
        var samples = new float[16_000];
        samples[8_000] = 0.5f;

        Assert.False(LocalWhisperRecognitionService.ContainsAudibleSignal(samples));
    }

    [Fact]
    public void NormalSpeechLevelSignalPassesThePreflightCheck()
    {
        var samples = Enumerable.Range(0, 16_000)
            .Select(index => 0.003f * MathF.Sin(2 * MathF.PI * 220 * index / 16_000))
            .ToArray();

        Assert.True(LocalWhisperRecognitionService.ContainsAudibleSignal(samples));
    }

    [Fact]
    public void LongLeadingSilenceDoesNotDiluteQuietSpeechLevelSignal()
    {
        var samples = new float[160_000];
        for (var index = 152_000; index < samples.Length; index++)
        {
            samples[index] = 0.003f * MathF.Sin(
                2 * MathF.PI * 220 * index / 16_000);
        }

        Assert.True(LocalWhisperRecognitionService.ContainsAudibleSignal(samples));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public sealed class LocalAudioConversionTests
{
    [Fact]
    public void SavedDeviceIdNeverFallsBackToAnotherDeviceWithTheSameName()
    {
        AudioInputDeviceInfo[] devices =
        [
            new("current-id", "USB-Mikrofon")
        ];

        Assert.False(AudioInputDeviceService.IsConfiguredMicrophoneActive(
            devices,
            "missing-id",
            "USB-Mikrofon"));
    }

    [Fact]
    public void LegacyNameFallbackRequiresAnUnambiguousDevice()
    {
        AudioInputDeviceInfo[] duplicateNames =
        [
            new("first-id", "USB-Mikrofon"),
            new("second-id", "USB-Mikrofon")
        ];

        Assert.False(AudioInputDeviceService.IsConfiguredMicrophoneActive(
            duplicateNames,
            deviceId: null,
            "USB-Mikrofon"));
        Assert.True(AudioInputDeviceService.IsConfiguredMicrophoneActive(
            duplicateNames[..1],
            deviceId: null,
            "USB-Mikrofon"));
    }

    [Fact]
    public void EmptyCaptureProducesNoSamples()
    {
        var result = LocalAudioCaptureSession.ConvertToMono16Khz(
            [],
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        Assert.Empty(result);
    }

    [Fact]
    public void Pcm16MonoAtTargetRateIsConvertedWithoutChangingLength()
    {
        short[] source = [-32768, -16384, 0, 16384, 32767];
        var bytes = new byte[source.Length * sizeof(short)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);

        var result = LocalAudioCaptureSession.ConvertToMono16Khz(
            bytes,
            new WaveFormat(16000, 16, 1));

        Assert.Equal(source.Length, result.Length);
        Assert.Equal(-1f, result[0], 3);
        Assert.Equal(0f, result[2], 3);
        Assert.InRange(result[^1], 0.99f, 1f);
    }

    [Fact]
    public void FloatStereoIsDownmixedAndResampledToSixteenKilohertz()
    {
        const int frames = 4800;
        var interleaved = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            interleaved[frame * 2] = 0.8f;
            interleaved[frame * 2 + 1] = 0.2f;
        }

        var bytes = new byte[interleaved.Length * sizeof(float)];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);

        var result = LocalAudioCaptureSession.ConvertToMono16Khz(
            bytes,
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        Assert.InRange(result.Length, 1590, 1610);
        Assert.All(result.Skip(20).Take(100), sample =>
            Assert.InRange(sample, 0.49f, 0.51f));
    }

    [Fact]
    public void DownmixPreservesTheOnlyActiveChannel()
    {
        const int frames = 1600;
        var interleaved = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            interleaved[frame * 2 + 1] = 0.65f;
        }

        var bytes = new byte[interleaved.Length * sizeof(float)];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);

        var result = LocalAudioCaptureSession.ConvertToMono16Khz(
            bytes,
            WaveFormat.CreateIeeeFloatWaveFormat(16000, 2));

        Assert.Equal(frames, result.Length);
        Assert.All(result, sample => Assert.InRange(sample, 0.649f, 0.651f));
    }

    [Fact]
    public void DownmixAlignsOppositePhaseChannelsInsteadOfCancellingThem()
    {
        const int frames = 1600;
        var interleaved = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            interleaved[frame * 2] = 0.6f;
            interleaved[frame * 2 + 1] = -0.6f;
        }

        var bytes = new byte[interleaved.Length * sizeof(float)];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);

        var result = LocalAudioCaptureSession.ConvertToMono16Khz(
            bytes,
            WaveFormat.CreateIeeeFloatWaveFormat(16000, 2));

        Assert.Equal(frames, result.Length);
        Assert.All(result, sample => Assert.InRange(sample, 0.599f, 0.601f));
    }

    [Fact]
    public void ConversionHonorsAnAlreadyCancelledToken()
    {
        var bytes = new byte[16000 * sizeof(float)];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LocalAudioCaptureSession.ConvertToMono16Khz(
                bytes,
                WaveFormat.CreateIeeeFloatWaveFormat(16000, 1),
                cancellation.Token));
    }

    [Fact]
    public void DownmixHonorsCancellationRaisedWhileReadingTheSource()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancellingSampleProvider(cancellation);
        var downmix = new LocalAudioCaptureSession.DownmixToMonoSampleProvider(
            source,
            cancellation.Token);

        Assert.Throws<OperationCanceledException>(() =>
            downmix.Read(new float[1600], 0, 1600));
    }

    private sealed class CancellingSampleProvider : ISampleProvider
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingSampleProvider(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(16000, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, 0.25f, offset, count);
            _cancellation.Cancel();
            return count;
        }
    }
}
