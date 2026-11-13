using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OneClickClose
{
    public static class ProcessNameUtility
    {
        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }
            string value = name.Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 4);
            }
            return value.ToLowerInvariant();
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            string value = path.Trim();
            try
            {
                return Path.GetFullPath(value);
            }
            catch
            {
                return value;
            }
        }

        public static bool SameName(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> NormalizeDistinct(IEnumerable<string> names)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names == null)
            {
                return result;
            }
            foreach (string name in names)
            {
                string normalized = Normalize(name);
                if (normalized.Length > 0 && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }
            return result;
        }

        public static string GetProcessPathSafe(Process process)
        {
            if (process == null)
            {
                return null;
            }
            try
            {
                return process.MainModule.FileName;
            }
            catch
            {
                return null;
            }
        }

        public static string GetProcessPathSafe(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return GetProcessPathSafe(process);
                }
            }
            catch
            {
                return null;
            }
        }

        public static int? GetSessionId(Process process)
        {
            if (process == null)
            {
                return null;
            }
            try
            {
                return process.SessionId;
            }
            catch
            {
                return null;
            }
        }

        public static bool ProcessExists(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        public static List<string> GetProfileNames(AppConfig config)
        {
            if (config == null || config.Profiles == null)
            {
                return new List<string>();
            }
            return config.Profiles.Keys
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
