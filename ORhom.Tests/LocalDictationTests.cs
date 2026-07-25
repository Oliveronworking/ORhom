using NAudio.Wave;

namespace ORhom.Tests;

public sealed class LocalDictationConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ORhom.LocalDictation.Tests",
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
    public void WidelySeparatedClickFramesAreNotMistakenForSpeech()
    {
        var samples = new float[16_000];
        samples[5 * 320 + 10] = 0.5f;
        samples[20 * 320 + 10] = 0.5f;
        samples[40 * 320 + 10] = 0.5f;

        Assert.False(LocalWhisperRecognitionService.ContainsAudibleSignal(samples));
    }

    [Fact]
    public void ThreeConsecutiveQuietSpeechFramesPassThePreflightCheck()
    {
        var samples = new float[16_000];
        Array.Fill(samples, 0.003f, 20 * 320, 3 * 320);

        Assert.True(LocalWhisperRecognitionService.ContainsAudibleSignal(samples));
    }

    [Fact]
    public void SubthresholdContentAloneDoesNotPassTheSpeechGate()
    {
        var samples = new float[16_000];
        Array.Fill(samples, 0.001f, 20 * 320, 20 * 320);

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

    [Fact]
    public void PreparationTrimsOnlyEdgeSilenceAndKeepsThreeHundredMillisecondsOfPadding()
    {
        var samples = new float[64_000];
        const int speechStart = 16_000;
        const int speechEndExclusive = 32_000;
        Array.Fill(samples, 0.003f, speechStart, speechEndExclusive - speechStart);

        var prepared = LocalWhisperRecognitionService.PrepareSamplesForTranscription(samples);

        Assert.Equal(25_600, prepared.Length);
        Assert.All(prepared[..4_800], sample => Assert.Equal(0f, sample));
        Assert.Equal(0.003f, prepared[4_800]);
        Assert.Equal(0.003f, prepared[^4_801]);
        Assert.All(prepared[^4_800..], sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void PreparationPreservesSubthresholdSyllablesAroundStrongerSpeech()
    {
        var samples = new float[96_000];
        const int quietLeadingStart = 16_000;
        const int quietLeadingEndExclusive = 24_000;
        const int strongerSpeechStart = 40_000;
        const int strongerSpeechEndExclusive = 48_000;
        const int quietTrailingStart = 64_000;
        const int quietTrailingEndExclusive = 72_000;
        Array.Fill(
            samples,
            0.001f,
            quietLeadingStart,
            quietLeadingEndExclusive - quietLeadingStart);
        Array.Fill(
            samples,
            0.003f,
            strongerSpeechStart,
            strongerSpeechEndExclusive - strongerSpeechStart);
        Array.Fill(
            samples,
            0.001f,
            quietTrailingStart,
            quietTrailingEndExclusive - quietTrailingStart);

        var prepared = LocalWhisperRecognitionService.PrepareSamplesForTranscription(samples);

        Assert.Equal(65_600, prepared.Length);
        Assert.All(prepared[..4_800], sample => Assert.Equal(0f, sample));
        Assert.Equal(0.001f, prepared[4_800]);
        Assert.Equal(0.003f, prepared[28_800]);
        Assert.Equal(0.001f, prepared[52_800]);
        Assert.Equal(0.001f, prepared[^4_801]);
        Assert.All(prepared[^4_800..], sample => Assert.Equal(0f, sample));
        Assert.True(LocalWhisperRecognitionService.ContainsAudibleSignal(prepared));
    }

    [Fact]
    public void PreparationReplacesNonFiniteSamplesWithoutMutatingTheCapture()
    {
        var samples = Enumerable.Repeat(0.003f, 16_000).ToArray();
        samples[100] = float.NaN;
        samples[200] = float.PositiveInfinity;
        samples[300] = float.NegativeInfinity;

        var prepared = LocalWhisperRecognitionService.PrepareSamplesForTranscription(samples);

        Assert.NotSame(samples, prepared);
        Assert.Equal(samples.Length, prepared.Length);
        Assert.Equal(0f, prepared[100]);
        Assert.Equal(0f, prepared[200]);
        Assert.Equal(0f, prepared[300]);
        Assert.True(float.IsNaN(samples[100]));
        Assert.True(float.IsPositiveInfinity(samples[200]));
        Assert.True(float.IsNegativeInfinity(samples[300]));
        Assert.All(prepared, sample => Assert.True(float.IsFinite(sample)));
    }

    [Theory]
    [InlineData("ORhom")]
    [InlineData("ChatGPT")]
    [InlineData("Codex")]
    [InlineData("GitHub")]
    [InlineData("Repository")]
    [InlineData("PowerShell")]
    [InlineData(".NET")]
    [InlineData("JSON")]
    [InlineData("Vulkan")]
    [InlineData("Flash Attention")]
    [InlineData("Whisper Large V3 Turbo")]
    [InlineData("Zwischenablage")]
    public void TechnicalInitialPromptContainsExpectedVocabulary(string term)
    {
        Assert.Contains(term, LocalWhisperRecognitionService.InitialPrompt);
    }

    [Fact]
    public void NativeWarmupUsesOneSecondOfFiniteAudibleAudio()
    {
        var samples = LocalWhisperRecognitionService.CreateWarmupSamples();

        Assert.Equal(16_000, samples.Length);
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample)));
        Assert.True(LocalWhisperRecognitionService.ContainsAudibleSignal(samples));
    }

    [Fact]
    public async Task NativeWarmupDrainConsumesAndDiscardsEverySegment()
    {
        var discardedSegments = await LocalWhisperRecognitionService.DrainWarmupAsync(
            CreateWarmupSegments(),
            CancellationToken.None);

        Assert.Equal(2, discardedSegments);
    }

    [Fact]
    public async Task NativeWarmupDrainHonorsCancellationBeforeEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LocalWhisperRecognitionService.DrainWarmupAsync(
                CreateWarmupSegments(),
                cancellation.Token));
    }

    private static async IAsyncEnumerable<string> CreateWarmupSegments()
    {
        yield return "discarded warm-up text";
        await Task.Yield();
        yield return "also discarded";
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
