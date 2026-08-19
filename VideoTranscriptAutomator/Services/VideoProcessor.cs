using System.Diagnostics;
using System.Threading;
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

        using var semaphore = new SemaphoreSlim(_settings.MaxConcurrency, _settings.MaxConcurrency);
        var tasks = new List<Task>();

        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(cancellationToken);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessFileAsync(folderId, video, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task ProcessFileAsync(string folderId, DriveFileInfo video, CancellationToken cancellationToken)
    {
        var txtName = Path.ChangeExtension(video.Name, ".txt");

        var txtExists = await _uiService.FileExistsInFolderAsync(folderId, txtName, cancellationToken);
        if (txtExists)
        {
            _logger.LogWarning("[SKIPPED] Transcription already exists on Drive: {TxtName}", txtName);
            return;
        }

        _logger.LogInformation("[START] Transcribing: {VideoName}", video.Name);

        string? downloadedPath = null;
        string? audioPath = null;
        string? txtPath = null;
        bool uploadSucceeded = false;

        try
        {
            downloadedPath = await _uiService.DownloadFileAsync(video.Id, video.Name, cancellationToken);
            var videoSize = new FileInfo(downloadedPath).Length;
            _logger.LogInformation("[FFMPEG] Video downloaded: {Size} bytes", videoSize);

            if (videoSize < _settings.DownloadMinSizeBytes)
            {
                throw new InvalidOperationException($"Downloaded file too small: {videoSize} bytes (min: {_settings.DownloadMinSizeBytes})");
            }

            audioPath = await ExtractAudioAsync(downloadedPath, cancellationToken);
            var audioSize = new FileInfo(audioPath).Length;
            _logger.LogInformation("[FFMPEG] Audio extracted: {Size} bytes ({Ratio}x smaller)",
                audioSize, videoSize / Math.Max(audioSize, 1));

            var audioBytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
            var audioFileName = Path.GetFileName(audioPath);
            var result = await _transcriptionService.TranscribeAsync(audioBytes, audioFileName, cancellationToken);

            if (result.Success)
            {
                txtPath = Path.Combine(_settings.DownloadDirectory, txtName);
                await File.WriteAllTextAsync(txtPath, result.Transcription, cancellationToken);
                _logger.LogInformation("[SUCCESS] Written: {TxtPath}", txtPath);

                uploadSucceeded = await _uiService.UploadFileAsync(folderId, txtPath, cancellationToken);

                if (uploadSucceeded)
                {
                    _logger.LogInformation("[SUCCESS] Uploaded to Drive: {TxtName}", txtName);
                }
                else
                {
                    _logger.LogWarning("[WARN] Upload failed, keeping local file: {TxtPath}", txtPath);
                }
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
            SafeDelete(downloadedPath);
            SafeDelete(audioPath);

            if (uploadSucceeded)
            {
                SafeDelete(txtPath);
            }
        }
    }

    private static void SafeDelete(string? path)
    {
        if (path is not null)
            try { File.Delete(path); } catch { }
    }

    private async Task<string> ExtractAudioAsync(string videoPath, CancellationToken cancellationToken)
    {
        var audioPath = Path.ChangeExtension(videoPath, ".mp3");

        _logger.LogInformation("[FFMPEG] Extracting audio: {Video} -> {Audio}", Path.GetFileName(videoPath), Path.GetFileName(audioPath));

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-i \"{videoPath}\" -vn -acodec libmp3lame -q:a 4 -y \"{audioPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            _logger.LogError("[FFMPEG] Error: {Error}", stderr[..Math.Min(500, stderr.Length)]);
            throw new InvalidOperationException($"FFmpeg failed with exit code {process.ExitCode}");
        }

        if (!File.Exists(audioPath))
            throw new InvalidOperationException("FFmpeg did not create the audio file");

        return audioPath;
    }
}
