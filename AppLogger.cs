using System.IO;
using System.Security;
using System.Text;

namespace ORhom;

internal sealed class AppLogger
{
    internal const long MaximumLogFileBytes = 5 * 1024 * 1024;
    internal const string RotatedLogFileName = "app.previous.log";

    private const int MaximumMessageCharacters = 16 * 1024;
    private readonly object _gate = new();

    public AppLogger(string logDirectory)
    {
        LogPath = string.Empty;
        try
        {
            LogPath = Path.Combine(logDirectory, "app.log");
            Directory.CreateDirectory(logDirectory);
        }
        catch (Exception ex) when (IsRecoverableLoggingException(ex))
        {
            // Logging is best-effort and must never prevent the application from starting.
        }
    }

    public string LogPath { get; }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        var suffix = exception is null
            ? string.Empty
            : $"{Environment.NewLine}{exception}";
        Write("ERROR", message + suffix);
    }

    private void Write(string level, string message)
    {
        try
        {
            if (message.Length > MaximumMessageCharacters)
            {
                message = message[..MaximumMessageCharacters] + " … <truncated>";
            }

            var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
            lock (_gate)
            {
                RotateIfNeeded(Encoding.UTF8.GetByteCount(entry));
                File.AppendAllText(LogPath, entry);
            }
        }
        catch (Exception ex) when (IsRecoverableLoggingException(ex))
        {
            // A full disk, inaccessible directory or locked log file must not affect app behavior.
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(LogPath))
        {
            return;
        }

        var currentLength = new FileInfo(LogPath).Length;
        if (currentLength <= MaximumLogFileBytes - incomingBytes)
        {
            return;
        }

        var directory = Path.GetDirectoryName(LogPath) ?? string.Empty;
        var rotatedPath = Path.Combine(directory, RotatedLogFileName);
        File.Move(LogPath, rotatedPath, overwrite: true);
    }

    private static bool IsRecoverableLoggingException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;
}
