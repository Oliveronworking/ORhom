using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatGptDictationBridge;

internal sealed class OpenAITranscriptionProvider : ITranscriptionProvider
{
    private static readonly HttpClient HttpClient = new();
    private readonly AppLogger _logger;

    public OpenAITranscriptionProvider(AppLogger logger)
    {
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(string audioPath, AppSettings settings, CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey(settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key missing. Set OPENAI_API_KEY or openAIApiKey in settings.json.");
        }

        await using var audioStream = File.OpenRead(audioPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(audioStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(fileContent, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent(settings.TranscriptionModel), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            form.Add(new StringContent(settings.Language), "language");
        }

        request.Content = form;
        _logger.Info($"Transcription request started. Provider=openai Model='{settings.TranscriptionModel}'");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI transcription failed: {(int)response.StatusCode} {response.ReasonPhrase} {Truncate(body, 500)}");
        }

        var text = ExtractText(body).Trim();
        _logger.Info($"Transcription successful. TextLength={text.Length}");
        return text;
    }

    private static string? ResolveApiKey(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.OpenAIApiKey))
        {
            return settings.OpenAIApiKey;
        }

        return Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
               Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User) ??
               Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Machine);
    }

    private static Uri BuildEndpoint(AppSettings settings)
    {
        var baseUrl = settings.OpenAIApiBaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/v1/audio/transcriptions");
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string ExtractText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("text", out var textElement))
            {
                return textElement.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            return body;
        }

        return string.Empty;
    }
}
