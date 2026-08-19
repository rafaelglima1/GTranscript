namespace VideoTranscriptAutomator.Config;

public class AppSettings
{
    public string[] GoogleDriveFolderIds { get; set; } = [];
    public string ChromeUserDataPath { get; set; } = string.Empty;
    public string DownloadDirectory { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiEndpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string[] SupportedExtensions { get; set; } = [];
}
