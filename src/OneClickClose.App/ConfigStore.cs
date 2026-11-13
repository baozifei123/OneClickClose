using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OneClickClose
{
    public sealed class ConfigStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public string ConfigPath { get; private set; }
        public AppConfig Current { get; private set; }
        public bool LastSaveSucceeded { get; private set; }
        public string LastSaveError { get; private set; }
        public string LastLoadError { get; private set; }
        public string LastRestoreError { get; private set; }
        public string LastRestorePointPath { get; private set; }

        public ConfigStore(string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new ArgumentException("配置路径不能为空。", "configPath");
            }
            ConfigPath = configPath;
            Current = AppConfig.CreateDefault();
            LastSaveSucceeded = false;
            LastSaveError = string.Empty;
            LastLoadError = string.Empty;
            LastRestoreError = string.Empty;
            LastRestorePointPath = string.Empty;
        }

        public void Load()
        {
            LastLoadError = string.Empty;

            if (!File.Exists(ConfigPath))
            {
                Current = AppConfig.CreateDefault();
                Save();
                return;
            }

            try
            {
                string raw = File.ReadAllText(ConfigPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    Current = AppConfig.CreateDefault();
                    return;
                }

                AppConfig data = JsonSerializer.Deserialize<AppConfig>(raw, JsonOptions);
                if (data == null)
                {
                    throw new InvalidDataException("配置文件内容为空或格式不正确。");
                }
                Current = NormalizeLoaded(data);
            }
            catch (Exception ex)
            {
                LastLoadError = ex.Message;
                Current = AppConfig.CreateDefault();
            }
        }

        public bool Save(bool rotateBackup = true)
        {
            LastSaveSucceeded = false;
            LastSaveError = string.Empty;

            string tempPath = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupPath = ConfigPath + ".bak";
            string backupTempPath = backupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupReplacePath = backupPath + ".replace-" + Guid.NewGuid().ToString("N") + ".tmp";
            string configReplacePath = ConfigPath + ".replace-" + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                string directory = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                AppConfig payload = BuildPayload(Current);
                string json = JsonSerializer.Serialize(payload, JsonOptions);
                File.WriteAllText(tempPath, json, new UTF8Encoding(true));

                if (File.Exists(ConfigPath))
                {
                    if (rotateBackup)
                    {
                        // Keep the previous config as .bak, then atomically replace the live config.
                        File.Copy(ConfigPath, backupTempPath, false);
                        ReplaceFile(backupTempPath, backupPath, backupReplacePath);
                    }
                    ReplaceFile(tempPath, ConfigPath, configReplacePath);
                }
                else
                {
                    File.Move(tempPath, ConfigPath);
                }

                LastSaveSucceeded = true;
                LastLoadError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                return false;
            }
            finally
            {
                DeleteIfExists(tempPath);
                DeleteIfExists(backupTempPath);
                DeleteIfExists(backupReplacePath);
                DeleteIfExists(configReplacePath);
            }
        }

        public bool SaveProfile(string name, IEnumerable<string> protectedNames)
        {
            string cleanName = (name ?? string.Empty).Trim();
            if (cleanName.Length == 0)
            {
                LastSaveError = "预设名称不能为空。";
                return false;
            }

            string key = FindProfileKey(cleanName);
            if (key == null)
            {
                key = cleanName;
            }

            AppConfig snapshot = Current.Clone();
            Current.Profiles[key] = NormalizeDistinct(protectedNames);
            Current.ActiveProfile = key;
            if (!Save())
            {
                Current = snapshot;
                return false;
            }
            return true;
        }

        public bool ApplyProfile(string name)
        {
            string key = FindProfileKey(name);
            if (key == null)
            {
                LastSaveError = "预设不存在。";
                return false;
            }

            AppConfig snapshot = Current.Clone();
            Current.ProtectedProcesses = new List<string>(Current.Profiles[key]);
            Current.ActiveProfile = key;
            if (!Save())
            {
                Current = snapshot;
                return false;
            }
            return true;
        }

        public bool DeleteProfile(string name)
        {
            string key = FindProfileKey(name);
            if (key == null)
            {
                LastSaveError = "预设不存在。";
                return false;
            }

            AppConfig snapshot = Current.Clone();
            Current.Profiles.Remove(key);
            if (string.Equals(Current.ActiveProfile, key, StringComparison.OrdinalIgnoreCase))
            {
                Current.ActiveProfile = string.Empty;
            }
            if (!Save())
            {
                Current = snapshot;
                return false;
            }
            return true;
        }

        public int SetProtection(IEnumerable<string> processNames, bool protect)
        {
            List<string> names = NormalizeDistinct(processNames);
            if (names.Count == 0)
            {
                return 0;
            }

            AppConfig snapshot = Current.Clone();
            if (protect)
            {
                HashSet<string> existing = new HashSet<string>(
                    Current.ProtectedProcesses.Select(ProcessNameUtility.Normalize),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string name in names)
                {
                    if (existing.Add(name))
                    {
                        Current.ProtectedProcesses.Add(name);
                    }
                }
            }
            else
            {
                HashSet<string> removing = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                Current.ProtectedProcesses = Current.ProtectedProcesses
                    .Where(name => !removing.Contains(ProcessNameUtility.Normalize(name)))
                    .ToList();
            }

            Current.ActiveProfile = string.Empty;
            if (!Save())
            {
                Current = snapshot;
                return 0;
            }
            return names.Count;
        }

        public int SetPathProtection(IEnumerable<string> paths, bool protect)
        {
            List<string> normalized = NormalizePaths(paths);
            if (normalized.Count == 0)
            {
                return 0;
            }

            AppConfig snapshot = Current.Clone();
            if (Current.ProtectedPaths == null)
            {
                Current.ProtectedPaths = new List<string>();
            }

            if (protect)
            {
                HashSet<string> existing = new HashSet<string>(
                    Current.ProtectedPaths,
                    StringComparer.OrdinalIgnoreCase);
                foreach (string path in normalized)
                {
                    if (existing.Add(path))
                    {
                        Current.ProtectedPaths.Add(path);
                    }
                }
            }
            else
            {
                HashSet<string> removing = new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);
                Current.ProtectedPaths = Current.ProtectedPaths
                    .Where(path => !removing.Contains(ProcessNameUtility.NormalizePath(path)))
                    .ToList();
            }

            Current.ActiveProfile = string.Empty;
            if (!Save())
            {
                Current = snapshot;
                return 0;
            }
            return normalized.Count;
        }

        public bool RestoreBackup()
        {
            LastRestoreError = string.Empty;
            LastRestorePointPath = string.Empty;

            string backupPath = ConfigPath + ".bak";
            if (!File.Exists(backupPath))
            {
                LastRestoreError = "还没有配置备份。修改保护名单或预设后会自动生成 config.json.bak。";
                return false;
            }

            string restoreTempPath = ConfigPath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            string preRestorePath = ConfigPath + ".pre-restore-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                string backupJson = File.ReadAllText(backupPath, Encoding.UTF8);
                AppConfig backupData = JsonSerializer.Deserialize<AppConfig>(backupJson, JsonOptions);
                if (backupData == null)
                {
                    throw new InvalidDataException("配置备份格式不正确。");
                }

                File.Copy(backupPath, restoreTempPath, false);
                if (File.Exists(ConfigPath))
                {
                    File.Replace(restoreTempPath, ConfigPath, preRestorePath, true);
                    LastRestorePointPath = preRestorePath;
                }
                else
                {
                    File.Move(restoreTempPath, ConfigPath);
                }

                Load();
                if (!string.IsNullOrEmpty(LastLoadError))
                {
                    LastRestoreError = LastLoadError;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastRestoreError = ex.Message;
                return false;
            }
            finally
            {
                DeleteIfExists(restoreTempPath);
            }
        }

        public string FindProfileKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Current.Profiles == null)
            {
                return null;
            }
            foreach (string key in Current.Profiles.Keys)
            {
                if (string.Equals(key, name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
            return null;
        }

        private static AppConfig NormalizeLoaded(AppConfig data)
        {
            AppConfig config = new AppConfig();
            config.SchemaVersion = 2;
            config.Hotkey = string.IsNullOrWhiteSpace(data.Hotkey) ? "Ctrl+Alt+X" : data.Hotkey.Trim();
            config.GracefulTimeoutSeconds = data.GracefulTimeoutSeconds >= 1 && data.GracefulTimeoutSeconds <= 60
                ? data.GracefulTimeoutSeconds
                : 5;
            config.ProtectedProcesses = NormalizePreservingCase(data.ProtectedProcesses);
            config.ProtectedPaths = NormalizePaths(data.ProtectedPaths);
            config.ShowAllWindows = data.ShowAllWindows;
            config.IncludeChildProcesses = data.IncludeChildProcesses;
            config.IncludeSuspendedUwp = data.IncludeSuspendedUwp;
            config.LastMode = string.Equals(data.LastMode, "all", StringComparison.OrdinalIgnoreCase)
                ? "all"
                : "windows";

            config.Profiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (data.Profiles != null)
            {
                foreach (KeyValuePair<string, List<string>> profile in data.Profiles)
                {
                    string profileName = (profile.Key ?? string.Empty).Trim();
                    if (profileName.Length == 0)
                    {
                        continue;
                    }
                    config.Profiles[profileName] = NormalizeDistinct(profile.Value);
                }
            }

            string activeProfile = (data.ActiveProfile ?? string.Empty).Trim();
            if (activeProfile.Length > 0)
            {
                foreach (string key in config.Profiles.Keys)
                {
                    if (string.Equals(key, activeProfile, StringComparison.OrdinalIgnoreCase))
                    {
                        config.ActiveProfile = key;
                        break;
                    }
                }
            }
            return config;
        }

        private static AppConfig BuildPayload(AppConfig source)
        {
            AppConfig payload = source == null ? AppConfig.CreateDefault() : source.Clone();
            payload.SchemaVersion = 2;
            payload.Hotkey = string.IsNullOrWhiteSpace(payload.Hotkey) ? "Ctrl+Alt+X" : payload.Hotkey.Trim();
            if (payload.GracefulTimeoutSeconds < 1 || payload.GracefulTimeoutSeconds > 60)
            {
                payload.GracefulTimeoutSeconds = 5;
            }
            payload.LastMode = string.Equals(payload.LastMode, "all", StringComparison.OrdinalIgnoreCase)
                ? "all"
                : "windows";
            payload.ProtectedProcesses = NormalizePreservingCase(payload.ProtectedProcesses);
            payload.ProtectedPaths = NormalizePaths(payload.ProtectedPaths);

            Dictionary<string, List<string>> profiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (payload.Profiles != null)
            {
                foreach (KeyValuePair<string, List<string>> profile in payload.Profiles)
                {
                    string name = (profile.Key ?? string.Empty).Trim();
                    if (name.Length > 0)
                    {
                        profiles[name] = NormalizeDistinct(profile.Value);
                    }
                }
            }
            payload.Profiles = profiles;

            if (string.IsNullOrEmpty(payload.ActiveProfile) ||
                !profiles.ContainsKey(payload.ActiveProfile))
            {
                payload.ActiveProfile = string.Empty;
            }
            return payload;
        }

        private static List<string> NormalizePreservingCase(IEnumerable<string> values)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
            {
                return result;
            }
            foreach (string value in values)
            {
                string trimmed = (value ?? string.Empty).Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }
            return result;
        }

        private static List<string> NormalizeDistinct(IEnumerable<string> values)
        {
            List<string> result = ProcessNameUtility.NormalizeDistinct(values);
            result.Sort(StringComparer.CurrentCultureIgnoreCase);
            return result;
        }

        private static List<string> NormalizePaths(IEnumerable<string> values)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
            {
                return result;
            }
            foreach (string value in values)
            {
                string normalized = ProcessNameUtility.NormalizePath(value);
                if (normalized.Length > 0 && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }
            return result;
        }

        private static void ReplaceFile(string source, string destination, string replaceBackup)
        {
            if (File.Exists(destination))
            {
                File.Replace(source, destination, replaceBackup, true);
            }
            else
            {
                File.Move(source, destination);
            }
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
