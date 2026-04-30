using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class CrashLogger
    {
        private static readonly object SyncRoot = new();
        private static bool _initialized;

        public static string PrimaryLogFolderPath => AppPaths.GetFolderPath("Logs");
        public static string PrimaryCrashLogPath => Path.Combine(PrimaryLogFolderPath, "crash.log");
        public static string PrimaryStartupLogPath => Path.Combine(PrimaryLogFolderPath, "startup.log");
        public static string EasyLogFolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cleario", "Logs");
        public static string EasyCrashLogPath => Path.Combine(EasyLogFolderPath, "crash.log");

        public static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogException(e.ExceptionObject as Exception, "AppDomain.CurrentDomain.UnhandledException", isFatal: e.IsTerminating);

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LogException(e.Exception, "TaskScheduler.UnobservedTaskException", isFatal: false);
                e.SetObserved();
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                LogMessage("Cleario process exited.", "lifecycle.log");

            LogStartup();
        }

        public static void RegisterXamlUnhandledException(Application app)
        {
            app.UnhandledException += (_, e) =>
                LogException(e.Exception, "Microsoft.UI.Xaml.Application.UnhandledException", isFatal: false);
        }

        public static void LogStartup()
        {
            WriteToAllLogs("startup.log", BuildStartupText());
        }

        public static void LogMessage(string message, string fileName = "app.log")
        {
            WriteToAllLogs(fileName, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
        }

        public static void LogException(Exception? exception, string source, bool isFatal = false)
        {
            WriteToAllLogs("crash.log", BuildCrashText(exception, source, isFatal));
        }

        public static string GetLogLocationText()
        {
            return $"Local appdata: {EasyLogFolderPath}";
        }

        public static void OpenLogFolder()
        {
            Directory.CreateDirectory(EasyLogFolderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = EasyLogFolderPath,
                UseShellExecute = true
            });
        }

        private static string BuildStartupText()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] Cleario started");
            AppendEnvironmentInfo(builder);
            builder.AppendLine();
            return builder.ToString();
        }

        private static string BuildCrashText(Exception? exception, string source, bool isFatal)
        {
            var builder = new StringBuilder();

            builder.AppendLine("============================================================");
            builder.AppendLine($"Crash time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            builder.AppendLine($"Source: {source}");
            builder.AppendLine($"Fatal: {isFatal}");

            if (exception == null)
            {
                builder.AppendLine("Managed exception: none was provided. This can happen when Windows or a native DLL ended the process before .NET could capture the real exception.");
            }
            else
            {
                AppendException(builder, exception, 0);
            }

            builder.AppendLine();
            builder.AppendLine("Environment");
            AppendEnvironmentInfo(builder);
            builder.AppendLine("============================================================");
            builder.AppendLine();

            return builder.ToString();
        }

        private static void AppendException(StringBuilder builder, Exception exception, int depth)
        {
            var prefix = depth == 0 ? string.Empty : new string(' ', depth * 2);
            builder.AppendLine($"{prefix}Exception type: {exception.GetType().FullName}");
            builder.AppendLine($"{prefix}Message: {exception.Message}");
            builder.AppendLine($"{prefix}HResult: 0x{exception.HResult:X8}");
            builder.AppendLine($"{prefix}Target site: {exception.TargetSite}");
            builder.AppendLine($"{prefix}Stack trace:");
            builder.AppendLine(exception.StackTrace ?? $"{prefix}No stack trace was available.");

            if (exception.Data.Count > 0)
            {
                builder.AppendLine($"{prefix}Data:");
                foreach (var key in exception.Data.Keys)
                    builder.AppendLine($"{prefix}{key}: {exception.Data[key]}");
            }

            if (exception.InnerException != null)
            {
                builder.AppendLine($"{prefix}Inner exception:");
                AppendException(builder, exception.InnerException, depth + 1);
            }
        }

        private static void AppendEnvironmentInfo(StringBuilder builder)
        {
            var process = Process.GetCurrentProcess();
            builder.AppendLine($"Version: {GetVersion()}");
            builder.AppendLine($"Process name: {process.ProcessName}");
            builder.AppendLine($"Process id: {Environment.ProcessId}");
            builder.AppendLine($"Process path: {GetProcessPath(process)}");
            builder.AppendLine($"Base directory: {AppContext.BaseDirectory}");
            builder.AppendLine($"Current directory: {Environment.CurrentDirectory}");
            builder.AppendLine($"Local data folder: {AppPaths.LocalFolderPath}");
            builder.AppendLine($"Primary crash log: {PrimaryCrashLogPath}");
            builder.AppendLine($"Easy crash log: {EasyCrashLogPath}");
            builder.AppendLine($"OS: {Environment.OSVersion}");
            builder.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            builder.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
            builder.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
            builder.AppendLine($"Machine name: {Environment.MachineName}");
            builder.AppendLine($"User interactive: {Environment.UserInteractive}");
            builder.AppendLine($"Thread id: {Environment.CurrentManagedThreadId}");
            builder.AppendLine($"Working set: {process.WorkingSet64}");
            builder.AppendLine($"Private memory: {process.PrivateMemorySize64}");
            builder.AppendLine($"GC memory: {GC.GetTotalMemory(false)}");
        }


        private static string GetProcessPath(Process process)
        {
            try
            {
                return Environment.ProcessPath ?? process.MainModule?.FileName ?? "Unknown";
            }
            catch
            {
                return Environment.ProcessPath ?? "Unknown";
            }
        }

        private static string GetVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                    ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private static void WriteToAllLogs(string fileName, string text)
        {
            lock (SyncRoot)
            {
                foreach (var folder in GetLogFolders())
                {
                    try
                    {
                        Directory.CreateDirectory(folder);
                        File.AppendAllText(Path.Combine(folder, fileName), text);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static IEnumerable<string> GetLogFolders()
        {
            var folders = new List<string>();

            try
            {
                folders.Add(PrimaryLogFolderPath);
            }
            catch
            {
            }

            try
            {
                folders.Add(EasyLogFolderPath);
            }
            catch
            {
            }

            return folders
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
