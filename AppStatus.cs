namespace ChatGptDictationBridge;

internal enum AppStatus
{
    Idle,
    Starting,
    Recording,
    Stopping,
    ReadingText,
    Pasting
}
