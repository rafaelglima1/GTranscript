using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using VideoTranscriptAutomator.Config;
using VideoTranscriptAutomator.Helpers;
using VideoTranscriptAutomator.Interfaces;
using VideoTranscriptAutomator.Services;

namespace VideoTranscriptAutomator;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.Sources.Clear();
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                config.AddEnvironmentVariables();
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<AppSettings>(context.Configuration.GetSection("AppSettings"));

                services.AddSingleton<IUiAutomationService, PlaywrightAutomationService>();

                services.AddSingleton<ITranscriptionService, WhisperTranscriptionService>();

                services.AddSingleton<IVideoProcessor, VideoProcessor>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddProvider(new RollingFileLoggerProvider(
                    Path.Combine(AppContext.BaseDirectory, "logs")));
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .UseConsoleLifetime()
            .Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("VideoTranscriptAutomator starting...");

        var settings = host.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AppSettings>>().Value;
        logger.LogInformation("Session: {Session}", settings.PlaywrightSessionPath);
        logger.LogInformation("Google Drive folders: {Folders}",
            string.Join(", ", settings.GoogleDriveFolderIds));

        ValidateDependencies(logger);

        var processor = host.Services.GetRequiredService<IVideoProcessor>();

        foreach (var folderId in settings.GoogleDriveFolderIds)
        {
            if (!string.IsNullOrWhiteSpace(folderId))
            {
                try
                {
                    await processor.ProcessFolderAsync(folderId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[ERROR] Failed to process folder: {FolderId}", folderId);
                }
            }
        }

        logger.LogInformation("VideoTranscriptAutomator finished.");
    }

    private static void ValidateDependencies(ILogger<Program> logger)
    {
        var missing = new List<string>();

        var ffmpegPath = "C:\\ffmpeg\\bin\\ffmpeg.exe";
        if (!File.Exists(ffmpegPath))
            missing.Add("ffmpeg");

        var pythonPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "python.exe"),
            "python"
        };

        var pythonFound = pythonPaths.Any(p => File.Exists(p) || IsCommandInPath(p));
        if (!pythonFound)
            missing.Add("python");

        if (missing.Count > 0)
        {
            logger.LogCritical("[SETUP] Missing dependencies: {Missing}", string.Join(", ", missing));
            logger.LogCritical("[SETUP] Install them and try again.");
            Environment.Exit(1);
        }

        logger.LogInformation("[SETUP] Dependencies OK: ffmpeg, python");
    }

    private static bool IsCommandInPath(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c where {command}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(5000))
                return false;

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
