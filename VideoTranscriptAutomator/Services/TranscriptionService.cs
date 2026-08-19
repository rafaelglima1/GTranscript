using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using VideoTranscriptAutomator.Config;
using VideoTranscriptAutomator.Interfaces;
using VideoTranscriptAutomator.Models;

namespace VideoTranscriptAutomator.Services;

public class TranscriptionService : ITranscriptionService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TranscriptionService> _logger;
    private readonly AppSettings _settings;
    private readonly ResiliencePipeline _resiliencePipeline;

    public TranscriptionService(
        HttpClient httpClient,
        IOptions<AppSettings> settings,
        ResiliencePipeline resiliencePipeline,
        ILogger<TranscriptionService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _resiliencePipeline = resiliencePipeline;
        _logger = logger;
    }

    public async Task<TranscriptionResult> TranscribeAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[API] Sending {FileName} to transcription API...", fileName);

        return await _resiliencePipeline.ExecuteAsync(
            async ct => await SendTranscriptionRequestAsync(fileStream, fileName, ct),
            cancellationToken);
    }

    private async Task<TranscriptionResult> SendTranscriptionRequestAsync(Stream fileStream, string fileName, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(streamContent, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.ApiEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        request.Content = content;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var transcription = ExtractTranscription(responseBody);

        return TranscriptionResult.Ok(fileName, transcription);
    }

    private static string ExtractTranscription(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("text", out var textElement))
                return textElement.GetString() ?? string.Empty;

            if (doc.RootElement.TryGetProperty("transcription", out var transcriptionElement))
                return transcriptionElement.GetString() ?? string.Empty;

            return responseBody;
        }
        catch
        {
            return responseBody;
        }
    }
}
