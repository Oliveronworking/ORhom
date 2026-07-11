using Microsoft.Win32;

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

    public IReadOnlyList<string> GetActiveMicrophones()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(CaptureDevicesPath);
            if (root is null)
            {
                return [];
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
                var deviceDescription = properties?.GetValue(DeviceDescriptionProperty) as string;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    name = name.Trim();
                    if (!string.IsNullOrWhiteSpace(deviceDescription) &&
                        !name.Contains(deviceDescription, StringComparison.OrdinalIgnoreCase))
                    {
                        name = $"{name} ({deviceDescription.Trim()})";
                    }

                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Active microphones could not be enumerated.", ex);
        }

        var result = names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var signature = string.Join("\u001f", result);
        if (!signature.Equals(_lastDeviceSignature, StringComparison.Ordinal))
        {
            _lastDeviceSignature = signature;
            _logger.Info($"Active microphone devices changed. Count={result.Count}");
        }
        return result;
    }
}
