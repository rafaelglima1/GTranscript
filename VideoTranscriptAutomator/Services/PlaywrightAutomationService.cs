using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Text.Json;
using VideoTranscriptAutomator.Config;
using VideoTranscriptAutomator.Interfaces;

namespace VideoTranscriptAutomator.Services;

public class PlaywrightAutomationService : IUiAutomationService
{
    private readonly AppSettings _settings;
    private readonly ILogger<PlaywrightAutomationService> _logger;
    private IPlaywright? _playwright;
    private IBrowserContext? _browserContext;
    private IPage? _folderPage;
    private string? _currentFolderId;
    private readonly SemaphoreSlim _pageLock = new(1, 1);

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

        _logger.LogInformation("[PLAYWRIGHT] Session path: {Path}", _settings.PlaywrightSessionPath);

        _playwright = await Playwright.CreateAsync();

        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false,
            AcceptDownloads = true,
            Args =
            [
                "--disable-blink-features=AutomationControlled",
                "--no-first-run",
                "--no-default-browser-check"
            ]
        };

        if (!string.IsNullOrWhiteSpace(_settings.DownloadDirectory))
        {
            Directory.CreateDirectory(_settings.DownloadDirectory);
            options.DownloadsPath = _settings.DownloadDirectory;
        }

        _browserContext = await _playwright.Chromium.LaunchPersistentContextAsync(
            _settings.PlaywrightSessionPath,
            options);

        _logger.LogInformation("[PLAYWRIGHT] Browser ready (session: {Path})", _settings.PlaywrightSessionPath);
    }

    private async Task ScreenshotAsync(IPage page, string name)
    {
        try
        {
            var path = Path.Combine(_settings.DownloadDirectory, $"#printdebug_{name}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
            _logger.LogDebug("[SCREENSHOT] {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SCREENSHOT] Failed to capture {Name}", name);
        }
    }

    private async Task<IPage> EnsureFolderPageAsync(string folderId, CancellationToken cancellationToken = default)
    {
        if (_folderPage is not null && !_folderPage.IsClosed && _currentFolderId == folderId)
            return _folderPage;

        if (_folderPage is not null && !_folderPage.IsClosed)
            await _folderPage.CloseAsync();

        _folderPage = await _browserContext!.NewPageAsync();
        _currentFolderId = folderId;

        var url = $"https://drive.google.com/drive/folders/{folderId}";
        _logger.LogInformation("[GDRIVE] Opening folder: {Url}", url);

        await _folderPage.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30000
        });
        await _folderPage.WaitForTimeoutAsync(5000);

        var pageUrl = _folderPage.Url;
        if (pageUrl.Contains("accounts.google.com"))
        {
            _logger.LogWarning("[GDRIVE] Not logged in. Waiting up to 120s...");
            await ScreenshotAsync(_folderPage, "00_login_required");
            await _folderPage.WaitForURLAsync(u => !u.Contains("accounts.google.com"),
                new PageWaitForURLOptions { Timeout = 120000 });
            await _folderPage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });
            await _folderPage.WaitForTimeoutAsync(5000);
        }

        await ScreenshotAsync(_folderPage, "01_folder_loaded");
        return _folderPage;
    }

    private async Task<T> WithFolderPageAsync<T>(string folderId, Func<IPage, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await _pageLock.WaitAsync(cancellationToken);
        try
        {
            var page = await EnsureFolderPageAsync(folderId, cancellationToken);
            return await operation(page, cancellationToken);
        }
        finally
        {
            _pageLock.Release();
        }
    }

    public async Task<IReadOnlyList<DriveFileInfo>> ListFilesInFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        return await WithFolderPageAsync<IReadOnlyList<DriveFileInfo>>(folderId, async (page, ct) =>
        {
            var files = new List<DriveFileInfo>();

            var result = await page.EvaluateAsync<JsonElement>(@"() => {
                const results = [];
                const seen = new Set();
                document.querySelectorAll('[data-id]').forEach(el => {
                    const dataId = el.getAttribute('data-id');
                    if (!dataId || seen.has(dataId) || dataId === '_gd' || dataId.startsWith('ucc-')) return;
                    seen.add(dataId);

                    const ariaLabel = el.getAttribute('aria-label') || '';
                    const text = el.innerText || el.textContent || '';
                    const lines = text.split('\n').map(l => l.trim()).filter(l => l.length > 0);
                    const textName = lines[0] || '';

                    let name = textName;
                    if (ariaLabel) {
                        const parts = ariaLabel.split('\n');
                        if (parts.length > 0) name = parts[0].trim();
                    }

                    const isVideo = (el.querySelector('[aria-label*=""video"" i], [aria-label*=""mp4"" i], [aria-label*=""mov"" i], [aria-label*=""avi"" i], [aria-label*=""mkv"" i]') !== null
                        || el.getAttribute('data-file-type') === 'video'
                        || /\.(mp4|mov|avi|mkv|wmv|flv|webm|m4v)$/i.test(name))
                        && !/\.(txt|doc|pdf|jpg|png|gif|mp3|wav)$/i.test(name);

                    if (name) results.push({ dataId, name, isVideo });
                });
                return results;
            }");

            await ScreenshotAsync(page, "02_files_extracted");

            if (result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    var dataId = item.GetProperty("dataId").GetString() ?? "";
                    var name = item.GetProperty("name").GetString() ?? "";
                    var isVideo = item.GetProperty("isVideo").GetBoolean();

                    if (isVideo)
                    {
                        files.Add(new DriveFileInfo(dataId, name));
                        _logger.LogInformation("[GDRIVE] Video: {Name} (ID: {Id})", name, dataId);
                    }
                }
            }

            _logger.LogInformation("[GDRIVE] Found {Count} video(s) in folder {FolderId}", files.Count, folderId);
            return files;
        }, cancellationToken);
    }

    public async Task<bool> FileExistsInFolderAsync(string folderId, string fileName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        return await WithFolderPageAsync<bool>(folderId, async (page, ct) =>
        {
            var files = await page.EvaluateAsync<JsonElement>(@"() => {
                const results = new Set();
                document.querySelectorAll('[data-id]').forEach(el => {
                    const text = el.innerText || el.textContent || '';
                    const lines = text.split('\n').map(l => l.trim()).filter(l => l.length > 0);
                    const name = lines[0] || '';
                    if (name) results.add(name);
                });
                return Array.from(results);
            }");

            if (files.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in files.EnumerateArray())
                {
                    if (item.GetString() == fileName)
                    {
                        _logger.LogInformation("[GDRIVE] File exists on Drive: {FileName}", fileName);
                        return true;
                    }
                }
            }

            return false;
        }, cancellationToken);
    }

    public async Task<string> DownloadFileAsync(string fileId, string fileName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        _logger.LogInformation("[GDRIVE] Downloading: {FileName} (ID: {Id})", fileName, fileId);

        var downloadUrl = $"https://drive.google.com/uc?export=download&id={fileId}&confirm=t";
        var page = await _browserContext!.NewPageAsync();

        try
        {
            var downloadTask = page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = 300000 });

            try
            {
                await page.GotoAsync(downloadUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.Commit,
                    Timeout = 30000
                });
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Download is starting"))
            {
                _logger.LogDebug("[GDRIVE] Download started (expected exception)");
            }

            await ScreenshotAsync(page, "03_download_started");

            var download = await downloadTask;
            var suggestedName = download.SuggestedFilename;

            if (string.IsNullOrWhiteSpace(suggestedName) || !Path.HasExtension(suggestedName))
            {
                suggestedName = fileName;
            }

            var savePath = Path.Combine(_settings.DownloadDirectory, suggestedName);

            await download.SaveAsAsync(savePath);

            var downloadDir = _settings.DownloadDirectory;
            var originalDownloadPath = Path.Combine(downloadDir, download.SuggestedFilename);
            if (File.Exists(originalDownloadPath) && originalDownloadPath != savePath)
            {
                try { File.Delete(originalDownloadPath); } catch { }
            }

            foreach (var guidFile in Directory.GetFiles(downloadDir, "*"))
            {
                var name = Path.GetFileName(guidFile);
                if (name.Length == 36 && name.Count(c => c == '-') == 4 && Path.GetExtension(guidFile) == "")
                {
                    try { File.Delete(guidFile); } catch { }
                }
            }

            var fileInfo = new FileInfo(savePath);
            if (fileInfo.Length < _settings.DownloadMinSizeBytes)
            {
                throw new InvalidOperationException($"Downloaded file is too small: {fileInfo.Length} bytes (min: {_settings.DownloadMinSizeBytes})");
            }

            await ScreenshotAsync(page, "04_download_complete");

            _logger.LogInformation("[GDRIVE] Downloaded: {FileName} -> {Path} ({Size} bytes)",
                fileName, savePath, fileInfo.Length);
            return savePath;
        }
        finally
        {
            if (page is not null)
                await page.CloseAsync();
        }
    }

    public async Task<bool> UploadFileAsync(string folderId, string filePath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        return await WithFolderPageAsync<bool>(folderId, async (page, ct) =>
        {
            var fileName = Path.GetFileName(filePath);

            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(1000);

            _logger.LogInformation("[GDRIVE] Uploading: {FileName}", fileName);

            var newButton = await page.QuerySelectorAsync("[guidedhelpid='new_menu_button']");
            if (newButton is null)
            {
                _logger.LogError("[GDRIVE] Upload failed: 'Novo' button not found");
                return false;
            }

            await newButton.ClickAsync();
            await page.WaitForTimeoutAsync(1500);
            await ScreenshotAsync(page, "05_upload_menu_open");

            var fileUploadOption = await page.QuerySelectorAsync("text=Upload de arquivo")
                ?? await page.QuerySelectorAsync("text=File upload")
                ?? await page.QuerySelectorAsync("text=Fazer upload")
                ?? await page.QuerySelectorAsync("text=Carregar");

            if (fileUploadOption is null)
            {
                _logger.LogError("[GDRIVE] Upload failed: menu item not found");
                await page.Keyboard.PressAsync("Escape");
                return false;
            }

            var fileChooserTask = page.WaitForFileChooserAsync(new PageWaitForFileChooserOptions { Timeout = 30000 });

            await fileUploadOption.EvaluateAsync("el => el.click()");

            var fileChooser = await fileChooserTask;
            await ScreenshotAsync(page, "06_file_chooser_open");
            await fileChooser.SetFilesAsync(filePath);
            await page.WaitForTimeoutAsync(2000);

            var replaceDialog = await page.QuerySelectorAsync("button:has-text('Replace')")
                ?? await page.QuerySelectorAsync("button:has-text('Substituir')");

            if (replaceDialog is not null)
            {
                _logger.LogInformation("[GDRIVE] Replace dialog detected, canceling: {FileName}", fileName);
                await ScreenshotAsync(page, "07_replace_dialog");
                await page.Keyboard.PressAsync("Escape");
                await page.WaitForTimeoutAsync(1000);
                return true;
            }

            await ScreenshotAsync(page, "08_upload_in_progress");

            var maxWait = TimeSpan.FromSeconds(60);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < maxWait)
            {
                await page.WaitForTimeoutAsync(1000);

                var uploadDone = await page.EvaluateAsync<bool>(@"() => {
                    const toasts = document.querySelectorAll('[role=""status""]');
                    for (const t of toasts) {
                        const text = (t.innerText || t.textContent || '').toLowerCase();
                        if (text.includes('upload complete') || text.includes('upload conclu') ||
                            text.includes('concluído') || text.includes('carregado') ||
                            text.includes('uploads concluídos') || text.includes('1 item concluído')) return true;
                    }
                    const snackbars = document.querySelectorAll('.aLGju-R4o6n, .NsQWtf, [class*=""snackbar""]');
                    for (const s of snackbars) {
                        const text = (s.innerText || s.textContent || '').toLowerCase();
                        if (text.includes('complete') || text.includes('conclu')) return true;
                    }
                    const allElements = document.querySelectorAll('*');
                    for (const el of allElements) {
                        if (el.children.length === 0) {
                            const text = (el.innerText || '').trim();
                            if (text.includes('uploads concluídos') || text.includes('upload concluído')) return true;
                        }
                    }
                    return false;
                }");

                if (uploadDone)
                {
                    await ScreenshotAsync(page, "09_upload_confirmed");
                    _logger.LogInformation("[GDRIVE] Upload confirmed: {FileName}", fileName);
                    return true;
                }
            }

            await ScreenshotAsync(page, "10_upload_timeout");
            _logger.LogWarning("[GDRIVE] Upload timeout: {FileName}", fileName);
            return true;
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_folderPage is not null && !_folderPage.IsClosed)
                await _folderPage.CloseAsync();
        }
        catch { }

        try
        {
            if (_browserContext is not null)
            {
                await _browserContext.CloseAsync();
                await _browserContext.DisposeAsync();
            }
        }
        catch { }

        try { _playwright?.Dispose(); } catch { }

        _pageLock?.Dispose();
    }
}
