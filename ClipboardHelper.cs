using System.Collections.Specialized;
using System.IO;

namespace ORhom;

internal static class ClipboardHelper
{
    private const int ClipboardCaptureAttemptCount = 5;
    private const int ClipboardWriteAttemptCount = 4;

    public static bool TryCaptureStable(
        AppLogger logger,
        out IDataObject? snapshot,
        out uint sequenceNumber)
    {
        var captured = TryCaptureStableCore(
            NativeMethods.GetClipboardSequenceNumber,
            () => Capture(logger),
            Thread.Sleep,
            ClipboardCaptureAttemptCount,
            out snapshot,
            out sequenceNumber);
        if (!captured)
        {
            logger.Info($"Clipboard did not remain stable during snapshot capture. Attempts={ClipboardCaptureAttemptCount}");
        }

        return captured;
    }

    internal static bool TryCaptureStableCore<TSnapshot>(
        Func<uint> readSequenceNumber,
        Func<TSnapshot?> captureSnapshot,
        Action<int> delay,
        int maximumAttempts,
        out TSnapshot? snapshot,
        out uint sequenceNumber)
        where TSnapshot : class
    {
        ArgumentNullException.ThrowIfNull(readSequenceNumber);
        ArgumentNullException.ThrowIfNull(captureSnapshot);
        ArgumentNullException.ThrowIfNull(delay);
        maximumAttempts = Math.Max(maximumAttempts, 1);

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var before = readSequenceNumber();
            var candidate = captureSnapshot();
            var after = readSequenceNumber();
            if (candidate is not null && before == after)
            {
                snapshot = candidate;
                sequenceNumber = after;
                return true;
            }

            (candidate as IDisposable)?.Dispose();
            if (attempt < maximumAttempts)
            {
                delay(GetClipboardCaptureRetryDelayMs(attempt));
            }
        }

        snapshot = null;
        sequenceNumber = 0;
        return false;
    }

    internal static int GetClipboardCaptureRetryDelayMs(int failedAttempt) =>
        Math.Min(10 << Math.Clamp(failedAttempt - 1, 0, 3), 80);

    public static IDataObject? Capture(AppLogger logger)
    {
        DisposableDataObject? snapshot = null;
        try
        {
            var source = Clipboard.GetDataObject();
            if (source is null)
            {
                return new DisposableDataObject();
            }

            snapshot = new DisposableDataObject();
            var snapshotFailed = false;
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                try
                {
                    var data = source.GetData(format, autoConvert: false);
                    if (data is null)
                    {
                        snapshotFailed = true;
                        continue;
                    }

                    snapshot.SetData(format, autoConvert: false, CloneClipboardValue(data));
                }
                catch (Exception ex)
                {
                    logger.Error($"Clipboard format snapshot failed. Format='{format}'", ex);
                    snapshotFailed = true;
                }
            }

            if (snapshotFailed)
            {
                snapshot.Dispose();
                return null;
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            snapshot?.Dispose();
            logger.Error("Clipboard capture failed.", ex);
            return null;
        }
    }

    public static ClipboardRestoreOutcome Restore(
        IDataObject? dataObject,
        AppSettings settings,
        AppLogger logger,
        uint? expectedSequenceNumber = null)
    {
        if (!settings.RestoreClipboard)
        {
            return ClipboardRestoreOutcome.NotRequested;
        }

        if (dataObject is null)
        {
            logger.Info("Clipboard restore skipped because no safe snapshot is available.");
            return ClipboardRestoreOutcome.Failed;
        }

        for (var attempt = 1; attempt <= ClipboardWriteAttemptCount; attempt++)
        {
            if (expectedSequenceNumber is not null &&
                NativeMethods.GetClipboardSequenceNumber() != expectedSequenceNumber.Value)
            {
                logger.Info("Clipboard restore skipped because another application changed the clipboard.");
                return ClipboardRestoreOutcome.SkippedExternalChange;
            }

            try
            {
                if (dataObject.GetFormats(autoConvert: false).Length == 0)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetDataObject(dataObject, true);
                }

                logger.Info("Clipboard restored.");
                return ClipboardRestoreOutcome.Restored;
            }
            catch (Exception ex)
            {
                if (expectedSequenceNumber is not null &&
                    NativeMethods.GetClipboardSequenceNumber() != expectedSequenceNumber.Value)
                {
                    logger.Error("Clipboard restore failed after the clipboard changed during our restore attempt.", ex);
                    return ClipboardRestoreOutcome.Failed;
                }

                if (attempt == ClipboardWriteAttemptCount)
                {
                    logger.Error("Clipboard restore failed.", ex);
                    return ClipboardRestoreOutcome.Failed;
                }

                Thread.Sleep(20 * attempt);
            }
        }

        return ClipboardRestoreOutcome.Failed;
    }

    public static bool TrySetText(string text, AppLogger logger)
    {
        for (var attempt = 1; attempt <= ClipboardWriteAttemptCount; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == ClipboardWriteAttemptCount)
                {
                    logger.Error("Clipboard text update failed.", ex);
                    return false;
                }

                Thread.Sleep(20 * attempt);
            }
        }

        return false;
    }

    internal static object CloneClipboardValue(object value)
    {
        return value switch
        {
            Image image => image.Clone(),
            byte[] bytes => bytes.ToArray(),
            string[] paths => paths.ToArray(),
            StringCollection collection => CloneStringCollection(collection),
            MemoryStream stream => new MemoryStream(stream.ToArray(), writable: false),
            _ => value
        };
    }

    private static StringCollection CloneStringCollection(StringCollection source)
    {
        var clone = new StringCollection();
        clone.AddRange(source.Cast<string>().ToArray());
        return clone;
    }
}

internal sealed class DisposableDataObject : IDataObject, IDisposable
{
    private readonly DataObject _inner = new();
    private readonly HashSet<IDisposable> _ownedValues = new(
        ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public object? GetData(string format, bool autoConvert) =>
        _inner.GetData(format, autoConvert);

    public object? GetData(string format) => _inner.GetData(format);

    public object? GetData(Type format) => _inner.GetData(format);

    public bool GetDataPresent(string format, bool autoConvert) =>
        _inner.GetDataPresent(format, autoConvert);

    public bool GetDataPresent(string format) =>
        _inner.GetDataPresent(format);

    public bool GetDataPresent(Type format) =>
        _inner.GetDataPresent(format);

    public string[] GetFormats(bool autoConvert) =>
        _inner.GetFormats(autoConvert);

    public string[] GetFormats() => _inner.GetFormats();

    public void SetData(string format, bool autoConvert, object? data)
    {
        Track(data);
        _inner.SetData(format, autoConvert, data);
    }

    public void SetData(string format, object? data)
    {
        Track(data);
        _inner.SetData(format, data);
    }

    public void SetData(Type format, object? data)
    {
        Track(data);
        _inner.SetData(format, data);
    }

    public void SetData(object? data)
    {
        Track(data);
        _inner.SetData(data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var value in _ownedValues)
        {
            try
            {
                value.Dispose();
            }
            catch
            {
                // Clipboard cleanup is best-effort after the OS has copied the data.
            }
        }

        _ownedValues.Clear();
    }

    private void Track(object? data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data is IDisposable disposable)
        {
            _ownedValues.Add(disposable);
        }
    }
}

internal enum ClipboardRestoreOutcome
{
    NotRequested,
    Restored,
    SkippedExternalChange,
    Failed
}
