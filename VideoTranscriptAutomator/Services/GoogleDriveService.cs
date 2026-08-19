using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoTranscriptAutomator.Config;
using VideoTranscriptAutomator.Interfaces;
using File = Google.Apis.Drive.v3.Data.File;

namespace VideoTranscriptAutomator.Services;

public class GoogleDriveService : IGoogleDriveService
{
    private readonly Lazy<DriveService> _driveService;
    private readonly ILogger<GoogleDriveService> _logger;
    private readonly AppSettings _settings;

    public GoogleDriveService(IOptions<AppSettings> settings, ILogger<GoogleDriveService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        _driveService = new Lazy<DriveService>(() =>
        {
            var credential = Google.Apis.Auth.OAuth2.GoogleCredential
                .FromFile(_settings.GoogleCredentialsPath)
                .CreateScoped(DriveService.Scope.Drive);

            return new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "VideoTranscriptAutomator"
            });
        });
    }

    public async Task<IList<File>> ListVideosInFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[GDRIVE] Listing videos in folder: {FolderId}", folderId);

        var query = $"mimeType contains 'video/' AND '{folderId}' in parents AND trashed = false";
        var files = new List<File>();
        string? pageToken = null;

        do
        {
            var request = _driveService.Value.Files.List();
            request.Q = query;
            request.Fields = "nextPageToken, files(id, name, mimeType, size)";
            request.PageSize = 100;
            request.PageToken = pageToken;

            var response = await request.ExecuteAsync(cancellationToken);

            if (response.Files is not null)
            {
                files.AddRange(response.Files.Where(f =>
                    _settings.SupportedExtensions.Any(ext =>
                        f.Name?.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ?? false)));
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        _logger.LogInformation("[GDRIVE] Found {Count} video(s) in folder {FolderId}", files.Count, folderId);
        return files;
    }

    public async Task<bool> FileExistsInFolderAsync(string folderId, string fileName, CancellationToken cancellationToken = default)
    {
        var query = $"name = '{EscapeQuery(fileName)}' AND '{folderId}' in parents AND trashed = false";
        var request = _driveService.Value.Files.List();
        request.Q = query;
        request.Fields = "files(id)";
        request.PageSize = 1;

        var response = await request.ExecuteAsync(cancellationToken);
        var exists = response.Files?.Any() == true;

        if (exists)
            _logger.LogInformation("[GDRIVE] File already exists: {FileName} in folder {FolderId}", fileName, folderId);

        return exists;
    }

    public async Task<Stream> DownloadFileStreamAsync(string fileId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[GDRIVE] Downloading file: {FileId}", fileId);
        var request = _driveService.Value.Files.Get(fileId);
        var stream = new MemoryStream();
        await request.DownloadAsync(stream, cancellationToken);
        stream.Position = 0;
        return stream;
    }

    public async Task UploadTextFileAsync(string folderId, string fileName, string content, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[GDRIVE] Uploading: {FileName} to folder {FolderId}", fileName, folderId);

        var fileMetadata = new File
        {
            Name = fileName,
            Parents = [folderId],
            MimeType = "text/plain"
        };

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var request = _driveService.Value.Files.Create(fileMetadata, stream, "text/plain");
        request.Fields = "id, name";
        await request.UploadAsync(cancellationToken);

        _logger.LogInformation("[GDRIVE] Uploaded: {FileName} (ID: {FileId})", fileName, request.ResponseBody?.Id);
    }

    private static string EscapeQuery(string value) =>
        value.Replace("'", "\\'").Replace("\\", "\\\\");
}
