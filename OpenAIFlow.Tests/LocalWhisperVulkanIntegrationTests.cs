namespace ChatGptDictationBridge.Tests;

public sealed class LocalWhisperVulkanIntegrationTests
{
    [LocalWhisperVulkanIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task OfficialLargeV3TurboLoadsAndRunsInferenceThroughVulkan()
    {
        var modelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAIFlow",
            "models",
            "whisper.cpp");
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            "OpenAIFlow.WhisperVulkan.Tests",
            Guid.NewGuid().ToString("N"));
        var logger = new AppLogger(logDirectory);
        using var service = new LocalWhisperRecognitionService(
            new LocalWhisperModelManager(modelDirectory),
            logger);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromHours(2));

        await service.EnsureReadyAsync(progress: null, cancellation.Token);

        Assert.True(service.IsReady);
        var samples = Enumerable.Range(0, 16_000)
            .Select(index => 0.03f * MathF.Sin(2 * MathF.PI * 220 * index / 16_000))
            .ToArray();
        var transcription = await service.TranscribeGermanAsync(
            samples,
            cancellation.Token);
        Assert.NotNull(transcription);

        await service.UnloadAsync(cancellation.Token);
        Assert.False(service.IsReady);
    }
}

internal sealed class LocalWhisperVulkanIntegrationFactAttribute : FactAttribute
{
    public LocalWhisperVulkanIntegrationFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("OPENAIFLOW_RUN_WHISPER_VULKAN_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set OPENAIFLOW_RUN_WHISPER_VULKAN_INTEGRATION=1 to run the real model and GPU test.";
        }
    }
}
