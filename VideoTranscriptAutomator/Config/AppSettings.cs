namespace VideoTranscriptAutomator.Config;

public class AppSettings
{
    public string[] GoogleDriveFolderIds { get; set; } = [];
    public string PlaywrightSessionPath { get; set; } = string.Empty;
    public string DownloadDirectory { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiEndpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string[] SupportedExtensions { get; set; } = [];
    public int MaxConcurrency { get; set; } = 2;
    public int DownloadMinSizeBytes { get; set; } = 1024;
    public int UploadConfirmTimeoutSeconds { get; set; } = 30;
    public string WhisperModel { get; set; } = "base";
}
