using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoTranscriptAutomator.Helpers;

public static class ChromeProfileFinder
{
    public static void ListProfiles()
    {
        var profiles = GetProfiles();

        if (profiles.Count == 0)
        {
            Console.WriteLine("[ERROR] No Chrome profiles found.");
            return;
        }

        Console.WriteLine("=== Chrome Profiles ===");
        Console.WriteLine($"User Data: {GetUserDataPath()}\n");

        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            Console.WriteLine($"  [{i + 1}] {p.DirectoryName}: {p.DisplayName} | Email: {p.Email}");
        }

        Console.WriteLine("\nUse the profile number in appsettings.json or let the app pick automatically.");
    }

    public static string? PromptForProfile()
    {
        var profiles = GetProfiles();

        if (profiles.Count == 0)
        {
            Console.WriteLine("[ERROR] No Chrome profiles found.");
            return null;
        }

        Console.WriteLine("\n=== Chrome Profile Selection ===");
        Console.WriteLine($"User Data: {GetUserDataPath()}\n");

        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            Console.WriteLine($"  [{i + 1}] {p.DisplayName} | Email: {p.Email}");
        }

        Console.Write("\nEnter the profile number: ");

        while (true)
        {
            var input = Console.ReadLine()?.Trim();

            if (int.TryParse(input, out var choice) && choice >= 1 && choice <= profiles.Count)
            {
                var selected = profiles[choice - 1];
                var fullPath = Path.Combine(GetUserDataPath(), selected.DirectoryName);
                Console.WriteLine($"Selected: {selected.DisplayName} ({selected.Email})");
                Console.WriteLine($"Path: {fullPath}\n");
                return fullPath;
            }

            Console.Write($"Invalid number. Enter a value between 1 and {profiles.Count}: ");
        }
    }

    public static void SaveProfileToSettings(string profilePath)
    {
        var settingsPath = FindProjectSettingsPath();

        if (settingsPath is null)
        {
            Console.WriteLine("[WARN] appsettings.json not found, skipping save.");
            return;
        }

        var escapedPath = profilePath.Replace("\\", "\\\\");
        var json = File.ReadAllText(settingsPath);
        var updated = Regex.Replace(json,
            @"(""ChromeUserDataPath""\s*:\s*"")[^""]*("")",
            $"$1{escapedPath}$2");

        File.WriteAllText(settingsPath, updated);
        Console.WriteLine($"[OK] ChromeUserDataPath saved to: {settingsPath}");

        var binCopy = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(binCopy) && binCopy != settingsPath)
        {
            File.WriteAllText(binCopy, updated);
        }
    }

    private static string? FindProjectSettingsPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "appsettings.json");
            if (File.Exists(candidate))
            {
                var csproj = current.GetFiles("*.csproj").FirstOrDefault();
                if (csproj is not null)
                    return candidate;
            }
            current = current.Parent;
        }

        return null;
    }

    private static string GetUserDataPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data");

    private static List<ChromeProfileInfo> GetProfiles()
    {
        var userDataPath = GetUserDataPath();

        if (!Directory.Exists(userDataPath))
            return [];

        return Directory.GetDirectories(userDataPath)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return name.StartsWith("Profile") || name == "Default";
            })
            .OrderBy(d => d)
            .Select(d =>
            {
                var profileName = Path.GetFileName(d);
                var prefsPath = Path.Combine(d, "Preferences");
                var displayName = profileName;
                var email = "N/A";

                if (File.Exists(prefsPath))
                {
                    try
                    {
                        var json = File.ReadAllText(prefsPath);
                        using var doc = JsonDocument.Parse(json);

                        if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                            profile.TryGetProperty("name", out var name))
                        {
                            displayName = name.GetString() ?? profileName;
                        }

                        if (doc.RootElement.TryGetProperty("account_info", out var accountInfo) &&
                            accountInfo.GetArrayLength() > 0)
                        {
                            var firstAccount = accountInfo[0];
                            if (firstAccount.TryGetProperty("email", out var emailProp))
                            {
                                email = emailProp.GetString() ?? "N/A";
                            }
                        }
                    }
                    catch { }
                }

                return new ChromeProfileInfo(profileName, displayName, email);
            })
            .ToList();
    }

    private record ChromeProfileInfo(string DirectoryName, string DisplayName, string Email);
}
