namespace VideoTranscriptAutomator.Models;

public class TranscriptionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Transcription { get; init; } = string.Empty;
    public DateTime ProcessedAt { get; init; } = DateTime.UtcNow;

    public static TranscriptionResult Ok(string filePath, string transcription) =>
        new() { Success = true, FilePath = filePath, Transcription = transcription, Message = "Transcription completed." };

    public static TranscriptionResult Fail(string filePath, string message) =>
        new() { Success = false, FilePath = filePath, Message = message };
}
