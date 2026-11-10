using System;
using System.Collections.Generic;
using System.IO;

namespace KennyPCOptimizer
{
    public static class Config
    {
        public const string AppName = "Kenny PC Cleaner & Optimization Studio";
        public const string AppShortName = "KennyPCOptimizer";
        public const string AppVersion = "2.0.3";

        public static string AppRootLocal
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppShortName
                );
            }
        }

        public static string RuntimeProfileDir
        {
            get { return Path.Combine(AppRootLocal, "AppRuntime"); }
        }

        public static string AppDataDir
        {
            get { return Path.Combine(AppRootLocal, "Data"); }
        }

        public static string StatsFile
        {
            get { return Path.Combine(AppDataDir, "stats.json"); }
        }

        public static string SettingsFile
        {
            get { return Path.Combine(AppDataDir, "settings.json"); }
        }

        public static string DefaultBackupDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Kenny_PC_Optimizer_Backups"
                );
            }
        }

        public static readonly HashSet<string> ProtectedRegTopKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "classes",
            "microsoft",
            "policies",
            "registeredapplications",
            "clients",
            "odbc",
            "wow6432node",
            "appdatalow",
            "windows",
            "windows defender",
            "mozilla",
            "mozillaplugins",
            "khronos",
            "vulkan",
            "directx",
            "intel",
            "amd",
            "nvidia corporation",
            "dotnet",
            "internet explorer",
            "systemcertificates"
        };

        public static void EnsureAppDirs()
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(DefaultBackupDir);
        }

        public static bool IsProtectedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            try
            {
                string norm = Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();
                string[] prefixes = new string[]
                {
                    Path.GetFullPath(DefaultBackupDir).TrimEnd('\\').ToLowerInvariant(),
                    Path.GetFullPath(AppRootLocal).TrimEnd('\\').ToLowerInvariant()
                };
                foreach (string prefix in prefixes)
                {
                    if (norm == prefix || norm.StartsWith(prefix + "\\"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return true;
            }
            return false;
        }
    }
}
