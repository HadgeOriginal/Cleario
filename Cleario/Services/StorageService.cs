using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class StorageService
    {
        private static readonly string FolderPath = AppPaths.GetFolderPath("Cleario");
        private static readonly string DatabasePath = Path.Combine(FolderPath, "cleario.db");
        private static readonly SemaphoreSlim DatabaseLock = new(1, 1);

        private static readonly JsonSerializerOptions PrettyJsonOptions = new()
        {
            WriteIndented = true
        };

        private sealed class StorageDatabase
        {
            public int Version { get; set; } = 1;
            public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
            public Dictionary<string, string> Documents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public static async Task SaveAsync<T>(string fileName, T data)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                var json = JsonSerializer.Serialize(data, PrettyJsonOptions);

                if (ShouldStoreInDatabase(fileName))
                {
                    await DatabaseLock.WaitAsync();
                    try
                    {
                        var database = await LoadDatabaseCoreAsync();
                        database.Documents[NormalizeDatabaseKey(fileName)] = json;
                        database.UpdatedUtc = DateTime.UtcNow;
                        await SaveDatabaseCoreAsync(database);
                        TryDeleteLegacyJsonFile(fileName);
                    }
                    finally
                    {
                        DatabaseLock.Release();
                    }

                    return;
                }

                var path = Path.Combine(FolderPath, fileName);
                await File.WriteAllTextAsync(path, json);
            }
            catch
            {
            }
        }

        public static async Task<T?> LoadAsync<T>(string fileName)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);

                if (ShouldStoreInDatabase(fileName))
                {
                    await DatabaseLock.WaitAsync();
                    try
                    {
                        var key = NormalizeDatabaseKey(fileName);
                        var database = await LoadDatabaseCoreAsync();

                        if (database.Documents.TryGetValue(key, out var dbJson) && !string.IsNullOrWhiteSpace(dbJson))
                            return JsonSerializer.Deserialize<T>(dbJson);

                        var legacyPath = Path.Combine(FolderPath, fileName);
                        if (!File.Exists(legacyPath))
                            return default;

                        var legacyJson = await File.ReadAllTextAsync(legacyPath);
                        if (string.IsNullOrWhiteSpace(legacyJson))
                            return default;

                        database.Documents[key] = legacyJson;
                        database.UpdatedUtc = DateTime.UtcNow;
                        await SaveDatabaseCoreAsync(database);
                        TryDeleteLegacyJsonFile(fileName);

                        return JsonSerializer.Deserialize<T>(legacyJson);
                    }
                    finally
                    {
                        DatabaseLock.Release();
                    }
                }

                var path = Path.Combine(FolderPath, fileName);
                if (!File.Exists(path))
                    return default;

                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return default;
            }
        }

        private static bool ShouldStoreInDatabase(string fileName)
        {
            return string.Equals(fileName, "history.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "library.json", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDatabaseKey(string fileName)
        {
            return Path.GetFileName(fileName ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static async Task<StorageDatabase> LoadDatabaseCoreAsync()
        {
            try
            {
                if (!File.Exists(DatabasePath))
                    return new StorageDatabase();

                var json = await File.ReadAllTextAsync(DatabasePath);
                var database = JsonSerializer.Deserialize<StorageDatabase>(json) ?? new StorageDatabase();
                database.Documents ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return database;
            }
            catch
            {
                return new StorageDatabase();
            }
        }

        private static async Task SaveDatabaseCoreAsync(StorageDatabase database)
        {
            database.Documents ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(database, PrettyJsonOptions);
            await File.WriteAllTextAsync(DatabasePath, json);
        }

        private static void TryDeleteLegacyJsonFile(string fileName)
        {
            try
            {
                var path = Path.Combine(FolderPath, fileName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
