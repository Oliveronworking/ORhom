namespace ChatGptDictationBridge;

internal interface ITranscriptionProvider
{
    Task<string> TranscribeAsync(string audioPath, AppSettings settings, CancellationToken cancellationToken);
}
