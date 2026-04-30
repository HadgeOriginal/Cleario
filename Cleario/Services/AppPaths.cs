using System;
using System.IO;
using Windows.Storage;

namespace Cleario.Services
{
    public static class AppPaths
    {
        private static readonly Lazy<string> _localFolderPath = new(GetLocalFolderPathCore);

        public static string LocalFolderPath => _localFolderPath.Value;

        public static string GetFilePath(string fileName)
        {
            Directory.CreateDirectory(LocalFolderPath);
            return Path.Combine(LocalFolderPath, fileName);
        }

        public static string GetFolderPath(string folderName)
        {
            var path = Path.Combine(LocalFolderPath, folderName);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string GetLocalFolderPathCore()
        {
            try
            {
                var packagedPath = ApplicationData.Current.LocalFolder.Path;
                if (!string.IsNullOrWhiteSpace(packagedPath))
                {
                    Directory.CreateDirectory(packagedPath);
                    return packagedPath;
                }
            }
            catch
            {
                
            }

            var fallbackPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cleario");

            Directory.CreateDirectory(fallbackPath);
            return fallbackPath;
        }
    }
}
