using NAudio.CoreAudioApi;

namespace ChatGptDictationBridge;

/// <summary>
/// Temporarily lowers other applications' playback sessions during a confirmed dictation.
/// Each session is restored to its individual pre-dictation volume afterwards.
/// </summary>
internal sealed class AudioDuckingService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AppLogger _logger;
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private bool _active;

    public AudioDuckingService(AppSettings settings, AppLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public void Begin()
    {
        if (!_settings.EnableAudioDucking || _active)
        {
            return;
        }

        _active = true;
        ApplyToNewSessions();
        _logger.Info($"Audio ducking started. VolumePercent={GetVolumePercent()} Sessions={_sessions.Count}");
    }

    public void Refresh()
    {
        if (_active)
        {
            ApplyToNewSessions();
        }
    }

    public void Restore()
    {
        if (!_active && _sessions.Count == 0)
        {
            return;
        }

        _active = false;
        var restoredCount = 0;
        Exception? restoreError = null;
        try
        {
            foreach (var snapshot in _sessions.Values)
            {
                try
                {
                    snapshot.Volume.Volume = snapshot.OriginalVolume;
                    restoredCount++;
                }
                catch (Exception ex)
                {
                    restoreError ??= ex;
                }
                finally
                {
                    snapshot.Control.Dispose();
                }
            }
        }
        finally
        {
            var failedCount = Math.Max(_sessions.Count - restoredCount, 0);
            _sessions.Clear();
            if (restoreError is not null)
            {
                _logger.Error("Audio session volumes could not be fully restored.", restoreError);
            }

            _logger.Info($"Audio ducking stopped. RestoredSessions={restoredCount} FailedSessions={failedCount}");
        }
    }

    public void Dispose() => Restore();

    private void ApplyToNewSessions()
    {
        var factor = GetVolumePercent() / 100f;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                using (endpoint)
                {
                    var endpointId = endpoint.ID;
                    var sessions = endpoint.AudioSessionManager.Sessions;
                    for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
                    {
                        var control = sessions[sessionIndex];
                        var retained = false;
                        try
                        {
                            var processId = control.GetProcessID;
                            var instanceId = GetStableSessionId(control, processId, sessionIndex);
                            var key = $"{endpointId}|{instanceId}";
                            if (processId == _currentProcessId || _sessions.ContainsKey(key))
                            {
                                continue;
                            }

                            var volume = control.SimpleAudioVolume;
                            var originalVolume = volume.Volume;
                            _sessions[key] = new SessionSnapshot(originalVolume, control, volume);
                            try
                            {
                                volume.Volume = Math.Clamp(originalVolume * factor, 0f, 1f);
                            }
                            catch
                            {
                                _sessions.Remove(key);
                                throw;
                            }

                            retained = true;
                        }
                        finally
                        {
                            if (!retained)
                            {
                                control.Dispose();
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Dictation continues even when an audio driver rejects session access.
            _logger.Error("Audio ducking refresh failed.", ex);
        }
    }

    private static string GetStableSessionId(AudioSessionControl control, uint processId, int sessionIndex)
    {
        try
        {
            var instanceId = control.GetSessionInstanceIdentifier;
            if (!string.IsNullOrWhiteSpace(instanceId))
            {
                return instanceId;
            }

            var sessionId = control.GetSessionIdentifier;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return sessionId;
            }
        }
        catch
        {
            // Some driver-owned and system sessions do not expose identifiers.
        }

        return $"pid:{processId}:slot:{sessionIndex}";
    }

    private int GetVolumePercent() => Math.Clamp(_settings.AudioDuckingVolumePercent, 0, 100);

    private sealed record SessionSnapshot(
        float OriginalVolume,
        AudioSessionControl Control,
        SimpleAudioVolume Volume);
}
