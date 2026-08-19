using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using VideoTranscriptAutomator.Config;
using VideoTranscriptAutomator.Interfaces;

namespace VideoTranscriptAutomator.Services;

public class PlaywrightAutomationService : IUiAutomationService
{
    private readonly AppSettings _settings;
    private readonly ILogger<PlaywrightAutomationService> _logger;
    private IPlaywright? _playwright;
    private IBrowserContext? _browserContext;

    public PlaywrightAutomationService(
        IOptions<AppSettings> settings,
        ILogger<PlaywrightAutomationService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_browserContext is not null)
            return;

        _logger.LogInformation("[PLAYWRIGHT] Initializing browser with Chrome profile: {Path}", _settings.ChromeUserDataPath);

        _playwright = await Playwright.CreateAsync();

        var launchOptions = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false,
            Channel = "chrome",
            AcceptDownloads = true,
            Args = ["--disable-blink-features=AutomationControlled"]
        };

        if (!string.IsNullOrWhiteSpace(_settings.DownloadDirectory))
        {
            Directory.CreateDirectory(_settings.DownloadDirectory);
            launchOptions.DownloadsPath = _settings.DownloadDirectory;
        }

        _browserContext = await _playwright.Chromium.LaunchPersistentContextAsync(
            _settings.ChromeUserDataPath,
            launchOptions);

        _logger.LogInformation("[PLAYWRIGHT] Browser initialized successfully");
    }

    public async Task<IReadOnlyList<DriveFileInfo>> ListFilesInFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var page = await _browserContext!.NewPageAsync();

        try
        {
            var url = $"https://drive.google.com/drive/folders/{folderId}";
            _logger.LogInformation("[GDRIVE] Navigating to: {Url}", url);
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForTimeoutAsync(2000);

            var files = new List<DriveFileInfo>();
            var rows = await page.QuerySelectorAllAsync("[role='row']");

            foreach (var row in rows)
            {
                var nameElement = await row.QuerySelectorAsync("[role='gridcell']:first-child");
                if (nameElement is null) continue;

                var name = await nameElement.GetAttributeAsync("aria-label");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var dataId = await row.GetAttributeAsync("data-id");

                var isFolder = await row.QuerySelectorAsync("[aria-label*='Folder']") is not null;
                if (isFolder) continue;

                var isVideo = _settings.SupportedExtensions.Any(ext =>
                    name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

                if (isVideo && !string.IsNullOrWhiteSpace(dataId))
                {
                    files.Add(new DriveFileInfo(dataId, name));
                }
            }

            _logger.LogInformation("[GDRIVE] Found {Count} video(s) in folder {FolderId}", files.Count, folderId);
            return files;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task<bool> FileExistsInFolderAsync(string folderId, string fileName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var page = await _browserContext!.NewPageAsync();

        try
        {
            var url = $"https://drive.google.com/drive/folders/{folderId}";
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForTimeoutAsync(2000);

            var nameElement = await page.QuerySelectorAsync($"[aria-label='{EscapeSelector(fileName)}']");
            var exists = nameElement is not null;

            if (exists)
                _logger.LogInformation("[GDRIVE] File already exists: {FileName}", fileName);

            return exists;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task<string> DownloadFileAsync(string folderId, string fileName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var page = await _browserContext!.NewPageAsync();

        try
        {
            var url = $"https://drive.google.com/drive/folders/{folderId}";
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForTimeoutAsync(2000);

            var fileElement = await page.QuerySelectorAsync($"[aria-label='{EscapeSelector(fileName)}']");
            if (fileElement is null)
                throw new InvalidOperationException($"File not found in Drive: {fileName}");

            await fileElement.ClickAsync();
            await page.WaitForTimeoutAsync(500);

            var downloadTask = page.WaitForDownloadAsync(new PageWaitForDownloadOptions
            {
                Timeout = 300000
            });

            await page.Keyboard.PressAsync("Shift+F10");
            await page.WaitForTimeoutAsync(500);

            var downloadOption = await page.QuerySelectorAsync("[aria-label='Download']");
            if (downloadOption is not null)
            {
                await downloadOption.ClickAsync();
            }
            else
            {
                await page.Keyboard.PressAsync("Escape");
                var moreBtn = await page.QuerySelectorAsync("[aria-label='More actions']");
                if (moreBtn is not null)
                {
                    await moreBtn.ClickAsync();
                    await page.WaitForTimeoutAsync(500);
                    var dlBtn = await page.QuerySelectorAsync("[role='menuitem']:has-text('Download')");
                    if (dlBtn is not null)
                        await dlBtn.ClickAsync();
                }
            }

            var download = await downloadTask;
            var suggestedPath = download.SuggestedFilename;
            var savePath = Path.Combine(_settings.DownloadDirectory, suggestedPath);

            await download.SaveAsAsync(savePath);

            _logger.LogInformation("[GDRIVE] Downloaded: {FileName} -> {Path}", fileName, savePath);
            return savePath;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task UploadFileAsync(string folderId, string filePath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var page = await _browserContext!.NewPageAsync();

        try
        {
            var url = $"https://drive.google.com/drive/folders/{folderId}";
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForTimeoutAsync(2000);

            var newBtn = await page.QuerySelectorAsync("[aria-label='New']");
            if (newBtn is not null)
            {
                await newBtn.ClickAsync();
                await page.WaitForTimeoutAsync(500);

                var uploadOption = await page.QuerySelectorAsync("[role='menuitem']:has-text('File upload')");
                if (uploadOption is not null)
                {
                    var fileChooserTask = page.WaitForFileChooserAsync();
                    await uploadOption.ClickAsync();
                    var fileChooser = await fileChooserTask;
                    await fileChooser.SetFilesAsync(filePath);
                    await page.WaitForTimeoutAsync(3000);
                }
            }

            _logger.LogInformation("[GDRIVE] Uploaded: {FileName}", Path.GetFileName(filePath));
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static string EscapeSelector(string value) =>
        value.Replace("'", "\\'").Replace("\\", "\\\\");

    public async ValueTask DisposeAsync()
    {
        if (_browserContext is not null)
        {
            await _browserContext.CloseAsync();
            await _browserContext.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
