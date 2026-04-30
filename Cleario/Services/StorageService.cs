using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class StorageService
    {
        private static readonly string FolderPath = AppPaths.GetFolderPath("Cleario");

        public static Task SaveAsync<T>(string fileName, T data)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                var path = Path.Combine(FolderPath, fileName);
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                return File.WriteAllTextAsync(path, json);
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public static async Task<T?> LoadAsync<T>(string fileName)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
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
    }
}
