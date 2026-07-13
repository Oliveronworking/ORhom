using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace ChatGptDictationBridge;

internal sealed class AudioInputDeviceService
{
    private const string CaptureDevicesPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    private const string FriendlyNameProperty = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string DeviceDescriptionProperty = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const int DeviceStateActive = 1;

    private readonly AppLogger _logger;
    private string _lastDeviceSignature = string.Empty;

    public AudioInputDeviceService(AppLogger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<AudioInputDeviceInfo> GetActiveMicrophoneDevices()
    {
        var devices = new Dictionary<string, AudioInputDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    var name = device.FriendlyName?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(device.ID) && !string.IsNullOrWhiteSpace(name))
                    {
                        devices[device.ID] = new AudioInputDeviceInfo(device.ID, name);
                    }
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Active microphones could not be enumerated through WASAPI; registry fallback will be used.", ex);
            AddRegistryDevices(devices);
        }

        var result = devices.Values
            .OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var signature = string.Join(
            "\u001f",
            result.Select(device => $"{device.Id}\u001e{device.DisplayName}"));
        if (!signature.Equals(_lastDeviceSignature, StringComparison.Ordinal))
        {
            _lastDeviceSignature = signature;
            _logger.Info($"Active microphone devices changed. Count={result.Count}");
        }

        return result;
    }

    public IReadOnlyList<string> GetActiveMicrophones() =>
        GetActiveMicrophoneDevices()
            .Select(device => device.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool IsMicrophoneActive(string? deviceId, string? displayName) =>
        IsConfiguredMicrophoneActive(
            GetActiveMicrophoneDevices(),
            deviceId,
            displayName);

    internal static bool IsConfiguredMicrophoneActive(
        IReadOnlyList<AudioInputDeviceInfo> activeDevices,
        string? deviceId,
        string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            return activeDevices.Any(device =>
                device.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        return activeDevices.Count(device =>
            device.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase)) == 1;
    }

    private static void AddRegistryDevices(IDictionary<string, AudioInputDeviceInfo> devices)
    {
        using var root = Registry.LocalMachine.OpenSubKey(CaptureDevicesPath);
        if (root is null)
        {
            return;
        }

        foreach (var deviceId in root.GetSubKeyNames())
        {
            using var device = root.OpenSubKey(deviceId);
            var state = Convert.ToInt32(device?.GetValue("DeviceState") ?? 0);
            if ((state & DeviceStateActive) == 0)
            {
                continue;
            }

            using var properties = device?.OpenSubKey("Properties");
            var name = properties?.GetValue(FriendlyNameProperty) as string;
            var description = properties?.GetValue(DeviceDescriptionProperty) as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            name = name.Trim();
            if (!string.IsNullOrWhiteSpace(description) &&
                !name.Contains(description, StringComparison.OrdinalIgnoreCase))
            {
                name = $"{name} ({description.Trim()})";
            }

            devices[deviceId] = new AudioInputDeviceInfo(deviceId, name);
        }
    }
}

internal sealed record AudioInputDeviceInfo(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
