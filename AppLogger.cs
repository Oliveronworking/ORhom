using System.IO;
using System.Security;
using System.Text;

namespace ORhom;

internal sealed class AppLogger : IDisposable
{
    internal const long MaximumLogFileBytes = 5 * 1024 * 1024;
    internal const string RotatedLogFileName = "app.previous.log";

    private const int MaximumMessageCharacters = 16 * 1024;
    private readonly object _gate = new();
    private FileStream? _stream;
    private long _currentLength;
    private volatile bool _isEnabled;

    public AppLogger(string logDirectory)
    {
        LogPath = string.Empty;
        try
        {
            LogPath = Path.Combine(logDirectory, "app.log");
            Directory.CreateDirectory(logDirectory);
            _isEnabled = true;
        }
        catch (Exception ex) when (IsRecoverableLoggingException(ex))
        {
            // Logging is best-effort and must never prevent the application from starting.
        }
    }

    public string LogPath { get; }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    public void Dispose()
    {
        lock (_gate)
        {
            _isEnabled = false;
            CloseStreamNoThrow();
        }
    }

    private void Write(string level, string message, Exception? exception = null)
    {
        if (!_isEnabled)
        {
            return;
        }

        try
        {
            message ??= string.Empty;
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            if (message.Length > MaximumMessageCharacters)
            {
                message = message[..MaximumMessageCharacters] + " … <truncated>";
            }

            var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
            var entryBytes = Encoding.UTF8.GetBytes(entry);
            WriteEntry(entryBytes);
        }
        catch (Exception ex) when (IsRecoverableLoggingException(ex))
        {
            // A full disk, inaccessible directory or locked log file must not affect app behavior.
            DisableLogging();
        }
    }

    private void WriteEntry(byte[] entryBytes)
    {
        lock (_gate)
        {
            if (!_isEnabled)
            {
                return;
            }

            try
            {
                EnsureStreamIsOpen();
                RotateIfNeeded(entryBytes.Length);
                _stream!.Write(entryBytes);
                _stream.Flush();
                _currentLength += entryBytes.Length;
            }
            catch (Exception ex) when (IsRecoverableLoggingException(ex))
            {
                _isEnabled = false;
                CloseStreamNoThrow();
            }
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (_currentLength <= MaximumLogFileBytes - incomingBytes)
        {
            return;
        }

        CloseStream();
        var directory = Path.GetDirectoryName(LogPath) ?? string.Empty;
        var rotatedPath = Path.Combine(directory, RotatedLogFileName);
        File.Move(LogPath, rotatedPath, overwrite: true);
        EnsureStreamIsOpen();
    }

    private void EnsureStreamIsOpen()
    {
        if (_stream is not null)
        {
            return;
        }

        _stream = new FileStream(
            LogPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete);
        _currentLength = _stream.Length;
        _stream.Seek(0, SeekOrigin.End);
    }

    private void DisableLogging()
    {
        lock (_gate)
        {
            _isEnabled = false;
            CloseStreamNoThrow();
        }
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
        _currentLength = 0;
    }

    private void CloseStreamNoThrow()
    {
        try
        {
            CloseStream();
        }
        catch (Exception ex) when (IsRecoverableLoggingException(ex))
        {
            _stream = null;
            _currentLength = 0;
        }
    }

    private static bool IsRecoverableLoggingException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;
}
