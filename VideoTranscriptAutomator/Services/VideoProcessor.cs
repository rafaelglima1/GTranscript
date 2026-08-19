using Microsoft.Extensions.Logging;
using VideoTranscriptAutomator.Interfaces;

namespace VideoTranscriptAutomator.Services;

public class VideoProcessor : IVideoProcessor
{
    private readonly IGoogleDriveService _googleDriveService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly ILogger<VideoProcessor> _logger;

    public VideoProcessor(
        IGoogleDriveService googleDriveService,
        ITranscriptionService transcriptionService,
        ILogger<VideoProcessor> logger)
    {
        _googleDriveService = googleDriveService;
        _transcriptionService = transcriptionService;
        _logger = logger;
    }

    public async Task ProcessFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[PROCESS] Scanning folder: {FolderId}", folderId);

        var videos = await _googleDriveService.ListVideosInFolderAsync(folderId, cancellationToken);

        if (videos.Count == 0)
        {
            _logger.LogWarning("[SKIPPED] No videos found in folder: {FolderId}", folderId);
            return;
        }

        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessFileAsync(video, cancellationToken);
        }
    }

    private async Task ProcessFileAsync(Google.Apis.Drive.v3.Data.File video, CancellationToken cancellationToken)
    {
        var videoName = video.Name ?? "unknown";
        var txtName = Path.ChangeExtension(videoName, ".txt");

        var txtExists = await _googleDriveService.FileExistsInFolderAsync(
            video.Parents?.First() ?? string.Empty,
            txtName,
            cancellationToken);

        if (txtExists)
        {
            _logger.LogWarning("[SKIPPED] Transcription already exists: {TxtName}", txtName);
            return;
        }

        _logger.LogInformation("[START] Transcribing: {VideoName} (ID: {FileId})", videoName, video.Id);

        try
        {
            using var stream = await _googleDriveService.DownloadFileStreamAsync(video.Id!, cancellationToken);
            var result = await _transcriptionService.TranscribeAsync(stream, videoName, cancellationToken);

            if (result.Success)
            {
                var folderId = video.Parents?.First() ?? string.Empty;
                await _googleDriveService.UploadTextFileAsync(folderId, txtName, result.Transcription, cancellationToken);
                _logger.LogInformation("[SUCCESS] Uploaded: {TxtName}", txtName);
            }
            else
            {
                _logger.LogError("[ERROR] {VideoName}: {Message}", videoName, result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ERROR] Failed to process {VideoName}", videoName);
        }
    }
}
