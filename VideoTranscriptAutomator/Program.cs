using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using VideoTranscriptAutomator.Config;
using VideoTranscriptAutomator.Helpers;
using VideoTranscriptAutomator.Interfaces;
using VideoTranscriptAutomator.Services;

namespace VideoTranscriptAutomator;

public class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--list-profiles"))
        {
            ChromeProfileFinder.ListProfiles();
            return;
        }

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables();

        var tempConfig = configBuilder.Build();
        var tempSettings = new AppSettings();
        tempConfig.GetSection("AppSettings").Bind(tempSettings);

        if (string.IsNullOrWhiteSpace(tempSettings.ChromeUserDataPath))
        {
            var selectedPath = ChromeProfileFinder.PromptForProfile();
            if (selectedPath is not null)
            {
                ChromeProfileFinder.SaveProfileToSettings(selectedPath);
                tempSettings.ChromeUserDataPath = selectedPath;
            }
        }

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

                services.PostConfigure<AppSettings>(options =>
                {
                    var llmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
                    if (!string.IsNullOrEmpty(llmApiKey))
                        options.ApiKey = llmApiKey;
                });

                services.AddSingleton<ResiliencePipeline>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<Program>>();
                    return new ResiliencePipelineBuilder()
                        .AddRetry(new RetryStrategyOptions
                        {
                            MaxRetryAttempts = 3,
                            Delay = TimeSpan.FromSeconds(2),
                            BackoffType = DelayBackoffType.Exponential,
                            ShouldHandle = new PredicateBuilder()
                                .Handle<HttpRequestException>()
                                .Handle<TimeoutRejectedException>(),
                            OnRetry = args =>
                            {
                                logger.LogWarning(
                                    "[RETRY] Attempt {Attempt} after {Delay}s (operation: {Operation})",
                                    args.AttemptNumber + 1,
                                    args.RetryDelay.TotalSeconds,
                                    args.Context.OperationKey);
                                return ValueTask.CompletedTask;
                            }
                        })
                        .Build();
                });

                services.AddSingleton<IUiAutomationService, PlaywrightAutomationService>();

                services.AddHttpClient<ITranscriptionService, TranscriptionService>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
                });

                services.AddSingleton<IVideoProcessor, VideoProcessor>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .UseConsoleLifetime()
            .Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("VideoTranscriptAutomator starting...");

        var settings = host.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AppSettings>>().Value;
        logger.LogInformation("Configured Google Drive folders: {Folders}",
            string.Join(", ", settings.GoogleDriveFolderIds));

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
}
