namespace ChatGptDictationBridge;

internal sealed class TranscriptionService
{
    private readonly ITranscriptionProvider _provider;

    public TranscriptionService(AppSettings settings, AppLogger logger)
    {
        if (!settings.TranscriptionProvider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            logger.Info($"Unsupported transcription provider '{settings.TranscriptionProvider}', falling back to OpenAI.");
        }

        _provider = new OpenAITranscriptionProvider(logger);
    }

    public Task<string> TranscribeAsync(string audioPath, AppSettings settings, CancellationToken cancellationToken)
    {
        return _provider.TranscribeAsync(audioPath, settings, cancellationToken);
    }
}
