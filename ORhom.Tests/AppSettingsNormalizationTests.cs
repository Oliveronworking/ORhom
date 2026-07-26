using System.Text.Json;

namespace ORhom.Tests;

public sealed class AppSettingsNormalizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ORhom.AppSettings.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadClampsUntrustedNumericSettingsToSafeRuntimeBounds()
    {
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            path,
            """
            {
              "recordingOverlayBottomOffsetPx": -2147483648,
              "pushToTalkHoldThresholdMs": 2147483647,
              "browserChromeExclusionTopPx": -1,
              "maxChatGptInputHeightPx": 2147483647,
              "maxChatGptInputWindowWidthRatio": 12.5,
              "recordingStateTimeoutMs": 2147483647,
              "dictationStopConfirmationTimeoutMs": 2147483647,
              "dictationResultTimeoutMs": 2147483647,
              "dictationResultPollIntervalMs": -1,
              "dictationSettleDelayMs": 2147483647,
              "dictationTextStableMs": -1,
              "dictationStopGracePeriodMs": 2147483647,
              "localMaxRecordingSeconds": 2147483647,
              "audioDuckingVolumePercent": -1,
              "pasteDelayMs": 2147483647,
              "restoreClipboardDelayMs": 2147483647
            }
            """);

        var settings = AppSettings.Load(
            path,
            new AppLogger(Path.Combine(_directory, "logs")));

        Assert.Equal(0, settings.RecordingOverlayBottomOffsetPx);
        Assert.Equal(2_000, settings.PushToTalkHoldThresholdMs);
        Assert.Equal(0, settings.BrowserChromeExclusionTopPx);
        Assert.Equal(2_000, settings.MaxChatGptInputHeightPx);
        Assert.Equal(1, settings.MaxChatGptInputWindowWidthRatio);
        Assert.Equal(5_000, settings.RecordingStateTimeoutMs);
        Assert.Equal(30_000, settings.DictationStopConfirmationTimeoutMs);
        Assert.Equal(600_000, settings.DictationResultTimeoutMs);
        Assert.Equal(100, settings.DictationResultPollIntervalMs);
        Assert.Equal(5_000, settings.DictationSettleDelayMs);
        Assert.Equal(3_000, settings.DictationTextStableMs);
        Assert.Equal(1_000, settings.DictationStopGracePeriodMs);
        Assert.Equal(600, settings.LocalMaxRecordingSeconds);
        Assert.Equal(0, settings.AudioDuckingVolumePercent);
        Assert.Equal(2_000, settings.PasteDelayMs);
        Assert.Equal(5_000, settings.RestoreClipboardDelayMs);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    public void InvalidOverlayCoordinatesAreDiscarded(string coordinate)
    {
        var path = Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            path,
            $$"""
            {
              "recordingOverlayMonitorDeviceName": "\\\\.\\DISPLAY2",
              "recordingOverlayRelativeX": {{coordinate}},
              "recordingOverlayRelativeY": 0.5
            }
            """);

        var settings = AppSettings.Load(
            path,
            new AppLogger(Path.Combine(_directory, "logs")));

        Assert.Equal(string.Empty, settings.RecordingOverlayMonitorDeviceName);
        Assert.Null(settings.RecordingOverlayRelativeX);
        Assert.Null(settings.RecordingOverlayRelativeY);
    }

    [Fact]
    public void SaveNormalizesValuesChangedByNonUiCallers()
    {
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{}");
        var logger = new AppLogger(Path.Combine(_directory, "logs"));
        var settings = AppSettings.Load(path, logger);
        settings.PasteDelayMs = int.MaxValue;

        Assert.True(settings.Save(logger));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            2_000,
            document.RootElement.GetProperty("pasteDelayMs").GetInt32());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
