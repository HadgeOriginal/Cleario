using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cleario.Services
{
    // Kept the old class name so the rest of the app does not need to change.
    // Runtime poster reads no longer touch ZipArchive. ZIP was slow for random scrolling reads
    // and could throw InvalidOperationException when reads/writes overlapped.
    public static class PosterZipCacheService
    {
        private const string LegacyEntryFolder = "posters/";
        private const string LegacyArchiveFileName = "posters.zip";
        private const string DatabaseFileName = "posters.db";
        private const string IndexFileName = "posters.index.json";
        private static readonly object SyncRoot = new();
        private static bool _initialized;
        private static PosterCacheIndex _index = new();

        private static string CacheFolderPath => AppPaths.GetFolderPath("PosterCache");
        private static string LegacyArchivePath => Path.Combine(CacheFolderPath, LegacyArchiveFileName);
        private static string DatabasePath => Path.Combine(CacheFolderPath, DatabaseFileName);
        private static string IndexPath => Path.Combine(CacheFolderPath, IndexFileName);
        private static string RuntimeFolderPath => AppPaths.GetFolderPath("PosterCacheRuntime");

        private sealed class PosterCacheIndex
        {
            public int Version { get; set; } = 2;
            public Dictionary<string, PosterCacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PosterCacheEntry
        {
            public string FileName { get; set; } = string.Empty;
            public string Extension { get; set; } = string.Empty;
            public long Offset { get; set; }
            public long Length { get; set; }
            public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
        }

        public static string TryGetCachedPosterUri(string id, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            if (IsLocalAppUri(url))
                return url;

            try
            {
                lock (SyncRoot)
                {
                    EnsureInitializedCore();

                    var key = CreateCacheKey(id, url);
                    if (!_index.Entries.TryGetValue(key, out var entry) || entry.Length <= 0)
                        return string.Empty;

                    var materialized = MaterializeEntryCore(key, entry);
                    if (string.IsNullOrWhiteSpace(materialized))
                        return string.Empty;

                    entry.LastAccessUtc = DateTime.UtcNow;
                    return materialized;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public static async Task<string> SaveAsync(string id, string url, byte[] bytes, string extension, long limitBytes)
        {
            if (string.IsNullOrWhiteSpace(url) || bytes == null || bytes.Length == 0 || string.IsNullOrWhiteSpace(extension))
                return string.Empty;

            if (IsLocalAppUri(url))
                return url;

            return await Task.Run(() =>
            {
                try
                {
                    lock (SyncRoot)
                    {
                        EnsureInitializedCore();

                        if (limitBytes > 0 && bytes.LongLength > limitBytes)
                            return string.Empty;

                        var key = CreateCacheKey(id, url);
                        if (_index.Entries.TryGetValue(key, out var existing) && existing.Length > 0)
                        {
                            var existingUri = MaterializeEntryCore(key, existing);
                            if (!string.IsNullOrWhiteSpace(existingUri))
                            {
                                existing.LastAccessUtc = DateTime.UtcNow;
                                SaveIndexCore();
                                return existingUri;
                            }
                        }

                        var normalizedExtension = NormalizeExtension(extension);
                        var fileName = key + normalizedExtension;
                        Directory.CreateDirectory(CacheFolderPath);
                        Directory.CreateDirectory(RuntimeFolderPath);

                        using (var database = new FileStream(DatabasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                        {
                            database.Seek(0, SeekOrigin.End);
                            var offset = database.Position;
                            database.Write(bytes, 0, bytes.Length);

                            _index.Entries[key] = new PosterCacheEntry
                            {
                                FileName = fileName,
                                Extension = normalizedExtension,
                                Offset = offset,
                                Length = bytes.LongLength,
                                LastAccessUtc = DateTime.UtcNow
                            };
                        }

                        EnforceLimitCore(limitBytes, 0);
                        SaveIndexCore();

                        return MaterializeEntryCore(key, _index.Entries[key]);
                    }
                }
                catch
                {
                    return string.Empty;
                }
            });
        }

        public static async Task EnforceLimitAsync(long limitBytes, long bytesNeeded)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (SyncRoot)
                    {
                        EnsureInitializedCore();
                        EnforceLimitCore(limitBytes, bytesNeeded);
                        SaveIndexCore();
                    }
                }
                catch
                {
                }
            });
        }

        private static bool IsLocalAppUri(string url)
        {
            return url.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureInitializedCore()
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(CacheFolderPath);
            Directory.CreateDirectory(RuntimeFolderPath);

            _index = LoadIndexCore();
            _index.Entries ??= new Dictionary<string, PosterCacheEntry>(StringComparer.OrdinalIgnoreCase);

            TryMigrateLegacyZipCore();
            TryMigrateLoosePosterFilesCore();
            TryDeleteOldRuntimeFilesCore();
            SaveIndexCore();

            _initialized = true;
        }

        private static PosterCacheIndex LoadIndexCore()
        {
            try
            {
                if (!File.Exists(IndexPath))
                    return new PosterCacheIndex();

                var json = File.ReadAllText(IndexPath);
                var index = JsonSerializer.Deserialize<PosterCacheIndex>(json) ?? new PosterCacheIndex();
                index.Entries ??= new Dictionary<string, PosterCacheEntry>(StringComparer.OrdinalIgnoreCase);
                index.Entries = new Dictionary<string, PosterCacheEntry>(index.Entries, StringComparer.OrdinalIgnoreCase);
                return index;
            }
            catch
            {
                return new PosterCacheIndex();
            }
        }

        private static void SaveIndexCore()
        {
            try
            {
                Directory.CreateDirectory(CacheFolderPath);
                _index.Version = 2;
                _index.Entries ??= new Dictionary<string, PosterCacheEntry>(StringComparer.OrdinalIgnoreCase);

                var json = JsonSerializer.Serialize(_index, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(IndexPath, json);
            }
            catch
            {
            }
        }

        private static void TryMigrateLegacyZipCore()
        {
            try
            {
                if (!File.Exists(LegacyArchivePath))
                    return;

                using var archive = ZipFile.OpenRead(LegacyArchivePath);
                foreach (var archiveEntry in archive.Entries
                             .Where(x => x.FullName.StartsWith(LegacyEntryFolder, StringComparison.OrdinalIgnoreCase) && x.Length > 0)
                             .ToList())
                {
                    try
                    {
                        var fileName = Path.GetFileName(archiveEntry.FullName);
                        if (string.IsNullOrWhiteSpace(fileName))
                            continue;

                        var key = Path.GetFileNameWithoutExtension(fileName);
                        if (string.IsNullOrWhiteSpace(key) || _index.Entries.ContainsKey(key))
                            continue;

                        using var source = archiveEntry.Open();
                        AppendEntryCore(key, source, archiveEntry.Length, Path.GetExtension(fileName));
                    }
                    catch
                    {
                    }
                }

                TryDeleteFile(LegacyArchivePath);
            }
            catch
            {
            }
        }

        private static void TryMigrateLoosePosterFilesCore()
        {
            try
            {
                var looseFiles = Directory
                    .EnumerateFiles(CacheFolderPath)
                    .Where(path =>
                        !string.Equals(Path.GetFileName(path), LegacyArchiveFileName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(Path.GetFileName(path), DatabaseFileName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(Path.GetFileName(path), IndexFileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var file in looseFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Length <= 0)
                        {
                            fileInfo.Delete();
                            continue;
                        }

                        var key = Path.GetFileNameWithoutExtension(fileInfo.Name);
                        if (!string.IsNullOrWhiteSpace(key) && !_index.Entries.ContainsKey(key))
                        {
                            using var source = File.OpenRead(file);
                            AppendEntryCore(key, source, fileInfo.Length, fileInfo.Extension);
                        }

                        fileInfo.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void AppendEntryCore(string key, Stream source, long length, string extension)
        {
            if (string.IsNullOrWhiteSpace(key) || source == null || length <= 0)
                return;

            var normalizedExtension = NormalizeExtension(extension);
            var fileName = key + normalizedExtension;

            Directory.CreateDirectory(CacheFolderPath);
            using var database = new FileStream(DatabasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            database.Seek(0, SeekOrigin.End);
            var offset = database.Position;
            source.CopyTo(database);

            _index.Entries[key] = new PosterCacheEntry
            {
                FileName = fileName,
                Extension = normalizedExtension,
                Offset = offset,
                Length = length,
                LastAccessUtc = DateTime.UtcNow
            };
        }

        private static string MaterializeEntryCore(string key, PosterCacheEntry entry)
        {
            try
            {
                if (entry == null || entry.Length <= 0 || entry.Offset < 0 || !File.Exists(DatabasePath))
                    return string.Empty;

                Directory.CreateDirectory(RuntimeFolderPath);

                var fileName = !string.IsNullOrWhiteSpace(entry.FileName)
                    ? entry.FileName
                    : key + NormalizeExtension(entry.Extension);

                var outputPath = Path.Combine(RuntimeFolderPath, Path.GetFileName(fileName));
                var shouldExtract = true;

                if (File.Exists(outputPath))
                {
                    var info = new FileInfo(outputPath);
                    shouldExtract = info.Length != entry.Length || info.Length <= 0;
                }

                if (shouldExtract)
                {
                    using var database = new FileStream(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (entry.Offset + entry.Length > database.Length)
                        return string.Empty;

                    database.Seek(entry.Offset, SeekOrigin.Begin);
                    using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    CopyExactBytes(database, output, entry.Length);
                }

                try { File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow); } catch { }

                return $"ms-appdata:///local/PosterCacheRuntime/{Uri.EscapeDataString(Path.GetFileName(outputPath))}";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void CopyExactBytes(Stream source, Stream destination, long bytesToCopy)
        {
            var buffer = new byte[81920];
            long remaining = bytesToCopy;

            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                    break;

                destination.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static void EnforceLimitCore(long limitBytes, long bytesNeeded)
        {
            if (limitBytes <= 0)
                return;

            if (bytesNeeded > limitBytes)
                return;

            var entries = _index.Entries
                .Where(x => x.Value != null && x.Value.Length > 0)
                .OrderBy(x => x.Value.LastAccessUtc)
                .ToList();

            long totalBytes = entries.Sum(x => Math.Max(0, x.Value.Length));
            var removedAny = false;

            foreach (var pair in entries)
            {
                if (totalBytes + bytesNeeded <= limitBytes)
                    break;

                totalBytes -= Math.Max(0, pair.Value.Length);
                TryDeleteRuntimeFile(pair.Value.FileName);
                _index.Entries.Remove(pair.Key);
                removedAny = true;
            }

            if (removedAny || IsDatabaseWasteHighCore())
                CompactDatabaseCore();
        }

        private static bool IsDatabaseWasteHighCore()
        {
            try
            {
                if (!File.Exists(DatabasePath))
                    return false;

                var logicalBytes = _index.Entries.Values.Sum(x => Math.Max(0, x.Length));
                var physicalBytes = new FileInfo(DatabasePath).Length;
                return physicalBytes > 64L * 1024L * 1024L && physicalBytes > logicalBytes * 2;
            }
            catch
            {
                return false;
            }
        }

        private static void CompactDatabaseCore()
        {
            try
            {
                if (!File.Exists(DatabasePath))
                    return;

                var tempPath = DatabasePath + ".tmp";
                TryDeleteFile(tempPath);

                using var oldDatabase = new FileStream(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var newDatabase = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var validKeys = _index.Entries.Keys.ToList();
                var buffer = new byte[81920];

                foreach (var key in validKeys)
                {
                    if (!_index.Entries.TryGetValue(key, out var entry) || entry.Length <= 0)
                        continue;

                    if (entry.Offset + entry.Length > oldDatabase.Length)
                    {
                        _index.Entries.Remove(key);
                        continue;
                    }

                    oldDatabase.Seek(entry.Offset, SeekOrigin.Begin);
                    var newOffset = newDatabase.Position;
                    long remaining = entry.Length;
                    while (remaining > 0)
                    {
                        var read = oldDatabase.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0)
                            break;

                        newDatabase.Write(buffer, 0, read);
                        remaining -= read;
                    }

                    entry.Offset = newOffset;
                }

                oldDatabase.Dispose();
                newDatabase.Dispose();

                File.Copy(tempPath, DatabasePath, overwrite: true);
                TryDeleteFile(tempPath);
            }
            catch
            {
            }
        }

        private static void TryDeleteOldRuntimeFilesCore()
        {
            try
            {
                var cutoffUtc = DateTime.UtcNow.AddDays(-3);
                foreach (var file in Directory.EnumerateFiles(RuntimeFolderPath))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc < cutoffUtc)
                            info.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteRuntimeFile(string? fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    return;

                var path = Path.Combine(RuntimeFolderPath, Path.GetFileName(fileName));
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return ".img";

            extension = extension.Trim();
            if (!extension.StartsWith(".", StringComparison.Ordinal))
                extension = "." + extension;

            return extension.Length > 12 ? ".img" : extension.ToLowerInvariant();
        }

        private static string CreateCacheKey(string id, string url)
        {
            var safeId = string.IsNullOrWhiteSpace(id) ? "poster" : MakeSafeFileName(id);
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
            return $"v3_{safeId}_{hash}";
        }

        private static string MakeSafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new StringBuilder(value.Length);

            foreach (var c in value)
                cleaned.Append(invalid.Contains(c) ? '_' : c);

            return cleaned.ToString();
        }
    }
}
