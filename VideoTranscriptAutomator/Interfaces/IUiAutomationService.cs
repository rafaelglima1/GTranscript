namespace VideoTranscriptAutomator.Interfaces;

public interface IUiAutomationService : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DriveFileInfo>> ListFilesInFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task<bool> FileExistsInFolderAsync(string folderId, string fileName, CancellationToken cancellationToken = default);
    Task<string> DownloadFileAsync(string folderId, string fileName, CancellationToken cancellationToken = default);
    Task UploadFileAsync(string folderId, string filePath, CancellationToken cancellationToken = default);
}

public record DriveFileInfo(string Id, string Name);
