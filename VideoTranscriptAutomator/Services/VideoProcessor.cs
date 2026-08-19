using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoTranscriptAutomator.Config;
using VideoTranscriptAutomator.Interfaces;

namespace VideoTranscriptAutomator.Services;

public class VideoProcessor : IVideoProcessor
{
    private readonly IUiAutomationService _uiService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly AppSettings _settings;
    private readonly ILogger<VideoProcessor> _logger;

    public VideoProcessor(
        IUiAutomationService uiService,
        ITranscriptionService transcriptionService,
        IOptions<AppSettings> settings,
        ILogger<VideoProcessor> logger)
    {
        _uiService = uiService;
        _transcriptionService = transcriptionService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ProcessFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[PROCESS] Scanning folder: {FolderId}", folderId);

        var videos = await _uiService.ListFilesInFolderAsync(folderId, cancellationToken);

        if (videos.Count == 0)
        {
            _logger.LogWarning("[SKIPPED] No videos found in folder: {FolderId}", folderId);
            return;
        }

        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessFileAsync(folderId, video, cancellationToken);
        }
    }

    private async Task ProcessFileAsync(string folderId, DriveFileInfo video, CancellationToken cancellationToken)
    {
        var txtName = Path.ChangeExtension(video.Name, ".txt");

        var txtExists = await _uiService.FileExistsInFolderAsync(folderId, txtName, cancellationToken);
        if (txtExists)
        {
            _logger.LogWarning("[SKIPPED] Transcription already exists: {TxtName}", txtName);
            return;
        }

        _logger.LogInformation("[START] Transcribing: {VideoName}", video.Name);

        string? downloadedPath = null;
        try
        {
            downloadedPath = await _uiService.DownloadFileAsync(folderId, video.Name, cancellationToken);

            await using var fileStream = File.OpenRead(downloadedPath);
            var result = await _transcriptionService.TranscribeAsync(fileStream, video.Name, cancellationToken);

            if (result.Success)
            {
                var txtPath = Path.Combine(_settings.DownloadDirectory, txtName);
                await File.WriteAllTextAsync(txtPath, result.Transcription, cancellationToken);
                _logger.LogInformation("[SUCCESS] Written locally: {TxtPath}", txtPath);

                await _uiService.UploadFileAsync(folderId, txtPath, cancellationToken);
                _logger.LogInformation("[SUCCESS] Uploaded to Drive: {TxtName}", txtName);

                try { File.Delete(txtPath); } catch { /* best effort cleanup */ }
            }
            else
            {
                _logger.LogError("[ERROR] {VideoName}: {Message}", video.Name, result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ERROR] Failed to process {VideoName}", video.Name);
        }
        finally
        {
            if (downloadedPath is not null)
            {
                try { File.Delete(downloadedPath); } catch { /* best effort cleanup */ }
            }
        }
    }
}
