using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.System;

namespace Cleario.Services
{
    public static class UpdateService
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/HadgeOriginal/Cleario/releases/latest";
        private const string RepositoryUrl = "https://github.com/HadgeOriginal/Cleario";
        private static readonly HttpClient _httpClient = CreateHttpClient();

        public sealed class ReleaseAsset
        {
            public string Name { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
        }

        public sealed class UpdateCheckResult
        {
            public bool Succeeded { get; set; }
            public bool IsUpdateAvailable { get; set; }
            public string CurrentVersion { get; set; } = string.Empty;
            public string LatestVersion { get; set; } = string.Empty;
            public string ReleaseUrl { get; set; } = RepositoryUrl;
            public string Message { get; set; } = string.Empty;
            public List<ReleaseAsset> Assets { get; set; } = new();
        }

        public sealed class UpdateInstallResult
        {
            public bool Succeeded { get; set; }
            public bool OpenedReleasePage { get; set; }
            public string Message { get; set; } = string.Empty;
            public string? DownloadedFilePath { get; set; }
        }

        public static string GetCurrentVersionText()
        {
            
            
            
            
            var informationalVersion = GetAssemblyInformationalVersionText();
            if (IsUsableVersionText(informationalVersion))
                return NormalizeVersionTextForDisplay(informationalVersion);

            var fileVersion = GetAssemblyFileVersionText();
            if (IsUsableVersionText(fileVersion))
                return NormalizeVersionTextForDisplay(fileVersion);

            var assemblyNameVersion = GetAssemblyNameVersionText();
            if (IsUsableVersionText(assemblyNameVersion))
                return NormalizeVersionTextForDisplay(assemblyNameVersion);

            var packageVersion = GetPackageVersionText();
            if (IsUsableVersionText(packageVersion))
                return NormalizeVersionTextForDisplay(packageVersion);

            return "Unknown";
        }

        public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            var currentVersionText = GetCurrentVersionText();
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersionText,
                ReleaseUrl = RepositoryUrl
            };

            try
            {
                using var response = await _httpClient.GetAsync(LatestReleaseApiUrl);
                if (!response.IsSuccessStatusCode)
                {
                    result.Message = "Could not check for updates right now.";
                    return result;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(stream);
                var root = document.RootElement;

                var tagName = GetString(root, "tag_name");
                var releaseName = GetString(root, "name");
                var htmlUrl = GetString(root, "html_url");
                var latestVersionText = !string.IsNullOrWhiteSpace(tagName) ? tagName : releaseName;

                result.Succeeded = true;
                result.LatestVersion = CleanVersionLabel(latestVersionText);
                result.ReleaseUrl = !string.IsNullOrWhiteSpace(htmlUrl) ? htmlUrl : RepositoryUrl;
                result.Assets = ReadAssets(root);

                if (!TryParseVersion(currentVersionText, out var currentVersion) || !TryParseVersion(latestVersionText, out var latestVersion))
                {
                    result.Message = $"Latest release: {result.LatestVersion}.";
                    result.IsUpdateAvailable = !string.Equals(
                        CleanVersionLabel(currentVersionText),
                        CleanVersionLabel(latestVersionText),
                        StringComparison.OrdinalIgnoreCase);
                    return result;
                }

                result.IsUpdateAvailable = latestVersion > currentVersion;
                result.Message = result.IsUpdateAvailable
                    ? $"Version {result.LatestVersion} is available."
                    : "Cleario is up to date.";

                return result;
            }
            catch
            {
                result.Message = "Could not check for updates right now.";
                return result;
            }
        }

        public static async Task<UpdateInstallResult> DownloadAndLaunchInstallerAsync(UpdateCheckResult update)
        {
            if (update == null)
            {
                return new UpdateInstallResult
                {
                    Message = "No update information was available."
                };
            }

            try
            {
                var asset = PickInstallerAsset(update.Assets);
                if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
                {
                    await OpenReleasePageAsync(update.ReleaseUrl);
                    return new UpdateInstallResult
                    {
                        Succeeded = true,
                        OpenedReleasePage = true,
                        Message = "No installer asset was found, so the GitHub release page was opened."
                    };
                }

                var downloadFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Cleario",
                    "Updates");
                Directory.CreateDirectory(downloadFolder);

                var safeName = MakeSafeFileName(asset.Name);
                var targetPath = Path.Combine(downloadFolder, safeName);

                await using (var downloadStream = await _httpClient.GetStreamAsync(asset.DownloadUrl))
                await using (var fileStream = File.Create(targetPath))
                {
                    await downloadStream.CopyToAsync(fileStream);
                }

                LaunchFile(targetPath);
                return new UpdateInstallResult
                {
                    Succeeded = true,
                    DownloadedFilePath = targetPath,
                    Message = "The update installer was downloaded and opened. Close Cleario if the installer asks you to."
                };
            }
            catch
            {
                try
                {
                    await OpenReleasePageAsync(update.ReleaseUrl);
                    return new UpdateInstallResult
                    {
                        Succeeded = true,
                        OpenedReleasePage = true,
                        Message = "The installer could not be launched automatically, so the GitHub release page was opened."
                    };
                }
                catch
                {
                    return new UpdateInstallResult
                    {
                        Message = "The update could not be downloaded or opened."
                    };
                }
            }
        }

        public static async Task OpenRepositoryPageAsync()
        {
            await OpenReleasePageAsync(RepositoryUrl);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Cleario-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static string GetAssemblyInformationalVersionText()
        {
            try
            {
                return Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                    ?.Split('+')[0]
                    ?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAssemblyFileVersionText()
        {
            try
            {
                return Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyFileVersionAttribute>()
                    ?.Version
                    ?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAssemblyNameVersionText()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetPackageVersionText()
        {
            try
            {
                var version = Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsUsableVersionText(string value)
        {
            var cleaned = CleanVersionLabel(value);
            return !string.IsNullOrWhiteSpace(cleaned) &&
                   !cleaned.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                   !cleaned.Equals("0.0.0", StringComparison.OrdinalIgnoreCase) &&
                   !cleaned.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetString(JsonElement root, string propertyName)
        {
            return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static List<ReleaseAsset> ReadAssets(JsonElement root)
        {
            var assets = new List<ReleaseAsset>();
            if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
                return assets;

            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var downloadUrl = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                    continue;

                assets.Add(new ReleaseAsset
                {
                    Name = name,
                    DownloadUrl = downloadUrl
                });
            }

            return assets;
        }

        private static ReleaseAsset? PickInstallerAsset(IEnumerable<ReleaseAsset> assets)
        {
            var candidates = assets
                .Where(x => IsInstallableAssetName(x.Name))
                .ToList();

            return candidates
                .OrderByDescending(x => x.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("install", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Name.EndsWith(".appinstaller", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }

        private static bool IsInstallableAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".appinstaller", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task OpenReleasePageAsync(string url)
        {
            var target = string.IsNullOrWhiteSpace(url) ? RepositoryUrl : url;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
                await Launcher.LaunchUriAsync(uri);
        }

        private static void LaunchFile(string path)
        {
            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            Process.Start(info);
        }

        private static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "ClearioUpdate.exe" : cleaned;
        }

        private static string CleanVersionLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unknown";

            var match = Regex.Match(value, @"\d+(?:\.\d+){0,3}");
            return match.Success ? match.Value : value.Trim();
        }

        private static string NormalizeVersionTextForDisplay(string value)
        {
            var normalized = NormalizeVersionTextForComparison(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return CleanVersionLabel(value);

            var parts = normalized.Split('.').ToList();
            while (parts.Count > 3 && parts[^1] == "0")
                parts.RemoveAt(parts.Count - 1);

            return string.Join('.', parts);
        }

        private static string NormalizeVersionTextForComparison(string value)
        {
            var cleaned = CleanVersionLabel(value);
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var parts = cleaned.Split('.').ToList();
            if (parts.Count == 4 && parts[0] == "0" && parts[1] == "0" && parts[2] != "0")
            {
                
                
                parts = new List<string> { "0", parts[2], parts[3] };
            }

            while (parts.Count < 4)
                parts.Add("0");

            return string.Join('.', parts.Take(4));
        }

        private static bool TryParseVersion(string value, out Version version)
        {
            version = new Version(0, 0, 0, 0);

            var normalized = NormalizeVersionTextForComparison(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            if (Version.TryParse(normalized, out var parsedVersion))
            {
                version = parsedVersion;
                return true;
            }

            return false;
        }
    }
}
