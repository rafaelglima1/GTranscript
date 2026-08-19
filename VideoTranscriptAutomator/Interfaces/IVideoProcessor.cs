namespace VideoTranscriptAutomator.Interfaces;

public interface IVideoProcessor
{
    Task ProcessFolderAsync(string folderId, CancellationToken cancellationToken = default);
}
