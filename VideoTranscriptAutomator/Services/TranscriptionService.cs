using System.Net.Http.Headers;
using System.Text;
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

    public async Task<TranscriptionResult> TranscribeAsync(byte[] fileBytes, string fileName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[API] Sending {FileName} ({Size} bytes) to transcription API...", fileName, fileBytes.Length);

        return await _resiliencePipeline.ExecuteAsync(
            async ct => await SendTranscriptionRequestAsync(fileBytes, fileName, ct),
            cancellationToken);
    }

    private async Task<TranscriptionResult> SendTranscriptionRequestAsync(byte[] fileBytes, string fileName, CancellationToken cancellationToken)
    {
        var base64Data = Convert.ToBase64String(fileBytes);
        var mimeType = GetMimeType(fileName);
        var dataUrl = $"data:{mimeType};base64,{base64Data}";

        var requestBody = new
        {
            model = _settings.Model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image_url",
                            image_url = new { url = dataUrl }
                        },
                        new
                        {
                            type = "text",
                            text = "Transcribe the audio from this file. Return only the transcription text, nothing else."
                        }
                    }
                }
            },
            max_tokens = 16384
        };

        var json = JsonSerializer.Serialize(requestBody);

        _logger.LogDebug("[API] Request body size: {Size} bytes", Encoding.UTF8.GetByteCount(json));

        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.ApiEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        request.Content = content;

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[API] Error {StatusCode}: {Response}", response.StatusCode, responseBody[..Math.Min(500, responseBody.Length)]);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation("[API] Response received ({Size} bytes)", responseBody.Length);
        var transcription = ExtractTranscription(responseBody);

        return TranscriptionResult.Ok(fileName, transcription);
    }

    private static string ExtractTranscription(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    return content.GetString() ?? string.Empty;
                }
            }

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

    private static string GetMimeType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
}
