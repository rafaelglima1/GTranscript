using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoTranscriptAutomator.Config;
using VideoTranscriptAutomator.Interfaces;
using VideoTranscriptAutomator.Models;

namespace VideoTranscriptAutomator.Services;

public class WhisperTranscriptionService : ITranscriptionService
{
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly AppSettings _settings;

    public WhisperTranscriptionService(
        IOptions<AppSettings> settings,
        ILogger<WhisperTranscriptionService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<TranscriptionResult> TranscribeAsync(byte[] fileBytes, string fileName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[WHISPER] Transcribing {FileName} ({Size} bytes) locally...", fileName, fileBytes.Length);

        var tempAudio = Path.Combine(Path.GetTempPath(), $"whisper_{Guid.NewGuid():N}.mp3");
        try
        {
            await File.WriteAllBytesAsync(tempAudio, fileBytes, cancellationToken);

            var scriptPath = Path.Combine(AppContext.BaseDirectory, "whisper_transcribe.py");
            if (!File.Exists(scriptPath))
            {
                var srcPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "whisper_transcribe.py");
                if (File.Exists(srcPath))
                    scriptPath = srcPath;
            }

            _logger.LogInformation("[WHISPER] Running whisper (model: {Model})...", _settings.WhisperModel);

            string stdout = string.Empty;
            const int maxAttempts = 2;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" {_settings.WhisperModel} \"{tempAudio}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi)!;
                stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode == 0)
                {
                    break;
                }

                if (attempt < maxAttempts - 1)
                {
                    _logger.LogWarning("[WHISPER] Attempt {Attempt} failed, retrying...", attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                _logger.LogError("[WHISPER] Python error: {Error}", stderr[..Math.Min(500, stderr.Length)]);
                return TranscriptionResult.Fail(fileName, $"Whisper failed: {stderr[..Math.Min(200, stderr.Length)]}");
            }

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var jsonLine = lines.LastOrDefault(l => l.TrimStart().StartsWith("{")) ?? lines.Last();

            if (string.IsNullOrWhiteSpace(jsonLine) || !jsonLine.TrimStart().StartsWith("{"))
            {
                _logger.LogError("[WHISPER] Invalid output from whisper: {Output}", stdout[..Math.Min(200, stdout.Length)]);
                return TranscriptionResult.Fail(fileName, "Whisper returned invalid output");
            }

            var result = JsonSerializer.Deserialize<JsonElement>(jsonLine.Trim());

            if (result.ValueKind != JsonValueKind.Object)
            {
                _logger.LogError("[WHISPER] Unexpected JSON type: {Kind}", result.ValueKind);
                return TranscriptionResult.Fail(fileName, "Whisper returned unexpected JSON");
            }

            if (!result.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            {
                _logger.LogError("[WHISPER] Missing or invalid 'segments' in output");
                return TranscriptionResult.Fail(fileName, "Whisper output missing segments");
            }

            var language = result.TryGetProperty("language", out var lang) ? lang.GetString() : "unknown";

            var formatted = FormatSegments(segments);
            _logger.LogInformation("[WHISPER] Transcription completed: {Length} chars (language: {Lang})", formatted.Length, language);

            return TranscriptionResult.Ok(fileName, formatted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WHISPER] Transcription failed for {FileName}", fileName);
            return TranscriptionResult.Fail(fileName, $"Whisper failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempAudio); } catch { }
        }
    }

    private static string FormatSegments(JsonElement segments)
    {
        var lines = new List<string>();

        foreach (var seg in segments.EnumerateArray())
        {
            var start = seg.GetProperty("start").GetDouble();
            var end = seg.GetProperty("end").GetDouble();
            var text = seg.GetProperty("text").GetString()?.Trim() ?? "";

            lines.Add($"{FormatTime(start)} - {FormatTime(end)} - {text}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
