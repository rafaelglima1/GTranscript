namespace VideoTranscriptAutomator.Interfaces;

public interface IGoogleDriveService
{
    Task<IList<Google.Apis.Drive.v3.Data.File>> ListVideosInFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task<bool> FileExistsInFolderAsync(string folderId, string fileName, CancellationToken cancellationToken = default);
    Task<Stream> DownloadFileStreamAsync(string fileId, CancellationToken cancellationToken = default);
    Task UploadTextFileAsync(string folderId, string fileName, string content, CancellationToken cancellationToken = default);
}
