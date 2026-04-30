using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class ImportExportService
    {
        public sealed class ExportOptions
        {
            public bool IncludeSettings { get; set; } = true;
            public bool IncludeAddons { get; set; } = true;
            public bool IncludeHistory { get; set; } = true;
            public bool IncludeLibrary { get; set; } = true;
        }

        public sealed class ExportPackage
        {
            public string Version { get; set; } = "1";
            public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
            public string SettingsJson { get; set; } = string.Empty;
            public string AddonsJson { get; set; } = string.Empty;
            public string HistoryJson { get; set; } = string.Empty;
            public string LibraryJson { get; set; } = string.Empty;
        }

        public static async Task<string> BuildExportJsonAsync(ExportOptions options)
        {
            if (options == null)
                options = new ExportOptions();
            var package = new ExportPackage
            {
                CreatedUtc = DateTime.UtcNow,
                SettingsJson = options.IncludeSettings ? SettingsManager.ExportJson() : string.Empty,
                AddonsJson = options.IncludeAddons ? AddonManager.ExportJson() : string.Empty,
                HistoryJson = options.IncludeHistory ? await HistoryService.ExportJsonAsync() : string.Empty,
                LibraryJson = options.IncludeLibrary ? await LibraryService.ExportJsonAsync() : string.Empty
            };

            return JsonSerializer.Serialize(package, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        public static async Task<bool> ImportFromJsonAsync(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                var package = JsonSerializer.Deserialize<ExportPackage>(json);
                if (package == null)
                    return false;

                if (!string.IsNullOrWhiteSpace(package.SettingsJson))
                    await SettingsManager.ImportJsonAsync(package.SettingsJson);

                if (!string.IsNullOrWhiteSpace(package.AddonsJson))
                    await AddonManager.ImportJsonAsync(package.AddonsJson);

                if (!string.IsNullOrWhiteSpace(package.HistoryJson))
                    await HistoryService.ImportJsonAsync(package.HistoryJson);

                if (!string.IsNullOrWhiteSpace(package.LibraryJson))
                    await LibraryService.ImportJsonAsync(package.LibraryJson);

                CatalogService.ClearTransientCaches();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
