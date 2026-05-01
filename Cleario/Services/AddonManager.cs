using Cleario.Models;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class AddonManager
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private static readonly string _storagePath = AppPaths.GetFilePath("addons.json");

        private const string CinemetaManifestUrl = "https://v3-cinemeta.strem.io/manifest.json";

        private static bool _initialized;
        private static readonly object _addonsGate = new();

        public static ObservableCollection<Addon> Addons { get; } = new();
        public static event EventHandler? AddonsChanged;

        private static void RaiseAddonsChanged()
        {
            CatalogService.ClearTransientCaches();
            AddonsChanged?.Invoke(null, EventArgs.Empty);
        }

        public static async Task InitializeAsync(bool forceReload = false)
        {
            if (_initialized && !forceReload)
                return;

            lock (_addonsGate)
                Addons.Clear();

            try
            {
                if (File.Exists(_storagePath))
                {
                    var json = await File.ReadAllTextAsync(_storagePath);
                    var items = JsonSerializer.Deserialize<AddonDto[]>(json);

                    if (items != null)
                    {
                        foreach (var item in items)
                        {
                            var normalized = NormalizeManifestUrl(item.ManifestUrl);
                            if (string.IsNullOrWhiteSpace(normalized))
                                continue;

                            if (string.Equals(normalized, CinemetaManifestUrl, StringComparison.OrdinalIgnoreCase))
                                continue;

                            lock (_addonsGate)
                            {
                                Addons.Add(new Addon
                                {
                                    Name = string.IsNullOrWhiteSpace(item.Name) ? "Addon" : item.Name,
                                    ManifestUrl = normalized,
                                    IsEnabled = item.IsEnabled
                                });
                            }
                        }

                        await PopulateAddonMetadataAsync(GetAddonsSnapshot());
                    }
                }
            }
            catch
            {
            }

            _initialized = true;
        }

        public static async Task<bool> AddAddonAsync(string manifestUrl)
        {
            await InitializeAsync();

            var normalized = NormalizeManifestUrl(manifestUrl);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            if (string.Equals(normalized, CinemetaManifestUrl, StringComparison.OrdinalIgnoreCase))
                return false;

            if (GetAddonsSnapshot().Any(a => string.Equals(a.ManifestUrl, normalized, StringComparison.OrdinalIgnoreCase)))
                return false;

            Addon addedAddon;
            lock (_addonsGate)
            {
                addedAddon = new Addon
                {
                    Name = "Loading...",
                    ManifestUrl = normalized,
                    IsEnabled = true
                };
                Addons.Add(addedAddon);
            }

            await SaveAsync();

            try
            {
                await PopulateAddonMetadataAsync(addedAddon);
                if (string.IsNullOrWhiteSpace(addedAddon.Name) || string.Equals(addedAddon.Name, "Loading...", StringComparison.OrdinalIgnoreCase))
                    addedAddon.Name = "Addon";

                await SaveAsync();
                RaiseAddonsChanged();
                return true;
            }
            catch
            {
                addedAddon.Name = "Addon";
                await SaveAsync();
                RaiseAddonsChanged();
                return true;
            }
        }

        public static async Task SetAddonEnabledAsync(Addon addon, bool isEnabled)
        {
            addon.IsEnabled = isEnabled;
            await SaveAsync();
            RaiseAddonsChanged();
        }

        public static async Task RemoveAddonAsync(Addon addon)
        {
            lock (_addonsGate)
            {
                if (Addons.Contains(addon))
                    Addons.Remove(addon);
            }

            await SaveAsync();
            RaiseAddonsChanged();
        }

        public static async Task MoveAddonUpAsync(Addon addon)
        {
            lock (_addonsGate)
            {
                var index = Addons.IndexOf(addon);
                if (index <= 0)
                    return;

                Addons.Move(index, index - 1);
            }

            await SaveAsync();
            RaiseAddonsChanged();
        }

        public static async Task MoveAddonDownAsync(Addon addon)
        {
            lock (_addonsGate)
            {
                var index = Addons.IndexOf(addon);
                if (index < 0 || index >= Addons.Count - 1)
                    return;

                Addons.Move(index, index + 1);
            }

            await SaveAsync();
            RaiseAddonsChanged();
        }

        public static async Task SaveReorderedAddonsAsync()
        {
            await SaveAsync();
            RaiseAddonsChanged();
        }

        public static IReadOnlyList<Addon> GetAddonsSnapshot(bool enabledOnly = false)
        {
            List<Addon> items;
            lock (_addonsGate)
                items = Addons.ToList();
            if (enabledOnly)
                items = items.Where(a => a.IsEnabled).ToList();

            return items;
        }

        public static async Task SaveAsync()
        {
            try
            {
                AddonDto[] items;
                lock (_addonsGate)
                {
                    items = Addons.Select(a => new AddonDto
                    {
                        Name = a.Name,
                        ManifestUrl = a.ManifestUrl,
                        IsEnabled = a.IsEnabled
                    }).ToArray();
                }

                var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(_storagePath, json);
            }
            catch
            {
            }
        }




        public static async Task ResetAsync()
        {
            try
            {
                lock (_addonsGate)
                    Addons.Clear();

                if (File.Exists(_storagePath))
                    File.Delete(_storagePath);
            }
            catch
            {
            }

            _initialized = true;
            await SaveAsync();
            RaiseAddonsChanged();
        }

        public static string ExportJson()
        {
            try
            {
                AddonDto[] items;
                lock (_addonsGate)
                {
                    items = Addons.Select(a => new AddonDto
                    {
                        Name = a.Name,
                        ManifestUrl = a.ManifestUrl,
                        IsEnabled = a.IsEnabled
                    }).ToArray();
                }

                return JsonSerializer.Serialize(items, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
            catch
            {
                return "[]";
            }
        }

        public static async Task ImportJsonAsync(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                    return;

                var items = JsonSerializer.Deserialize<AddonDto[]>(json) ?? Array.Empty<AddonDto>();
                await File.WriteAllTextAsync(_storagePath, JsonSerializer.Serialize(items, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

                _initialized = false;
                await InitializeAsync(forceReload: true);
                RaiseAddonsChanged();
            }
            catch
            {
            }
        }

        private static async Task PopulateAddonMetadataAsync(IEnumerable<Addon> addons)
        {
            var tasks = addons.Select(PopulateAddonMetadataAsync).ToArray();
            if (tasks.Length == 0)
                return;

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
            }
        }

        private static async Task PopulateAddonMetadataAsync(Addon addon)
        {
            if (addon == null || string.IsNullOrWhiteSpace(addon.ManifestUrl))
                return;

            try
            {
                var json = await _httpClient.GetStringAsync(addon.ManifestUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("name", out var nameProp))
                {
                    var displayName = nameProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(displayName))
                        addon.Name = displayName;
                }

                var hasConfig = false;

                if (root.TryGetProperty("behaviorHints", out var behaviorHints) && behaviorHints.ValueKind == JsonValueKind.Object)
                {
                    if (behaviorHints.TryGetProperty("configurable", out var configurableProp) && configurableProp.ValueKind == JsonValueKind.True)
                        hasConfig = true;

                    if (behaviorHints.TryGetProperty("configurationRequired", out var requiredProp) && requiredProp.ValueKind == JsonValueKind.True)
                        hasConfig = true;
                }

                if (!hasConfig && root.TryGetProperty("config", out var configProp) && configProp.ValueKind == JsonValueKind.Array && configProp.GetArrayLength() > 0)
                    hasConfig = true;

                addon.HasConfiguration = hasConfig;
                addon.ConfigurationUrl = hasConfig ? BuildConfigurationUrl(addon.ManifestUrl) : string.Empty;
            }
            catch
            {
                addon.HasConfiguration = false;
                addon.ConfigurationUrl = string.Empty;
            }
        }

        private static string BuildConfigurationUrl(string manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl))
                return string.Empty;

            var value = manifestUrl.Trim();
            var suffix = "/manifest.json";

            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return value.Substring(0, value.Length - suffix.Length).TrimEnd('/') + "/configure";

            var fileName = Path.GetFileName(value);
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var slashIndex = value.LastIndexOf('/');
                if (slashIndex >= 0)
                    return value.Substring(0, slashIndex).TrimEnd('/') + "/configure";
            }

            return value.TrimEnd('/') + "/configure";
        }

        private static string NormalizeManifestUrl(string? manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl))
                return string.Empty;

            var value = manifestUrl.Trim();

            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (value.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                return value;

            if (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return value;

            return value.TrimEnd('/') + "/manifest.json";
        }

        private sealed class AddonDto
        {
            public string Name { get; set; } = string.Empty;
            public string ManifestUrl { get; set; } = string.Empty;
            public bool IsEnabled { get; set; } = true;
        }
    }
}