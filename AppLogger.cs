using System.IO;

namespace ChatGptDictationBridge;

internal sealed class AppLogger
{
    private readonly object _gate = new();

    public AppLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        LogPath = Path.Combine(logDirectory, "app.log");
    }

    public string LogPath { get; }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        var suffix = exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}";
        Write("ERROR", message + suffix);
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (_gate)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
