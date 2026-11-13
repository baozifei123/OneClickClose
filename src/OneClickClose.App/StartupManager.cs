using System;
using System.IO;
using System.Windows.Forms;

namespace OneClickClose
{
    public static class StartupManager
    {
        private const string ShortcutName = "清场助手.lnk";
        private const string LegacyShortcutName = "一键收摊.lnk";
        private const string ShortcutDescription = "清场助手";

        public static string ShortcutPath
        {
            get { return BuildShortcutPath(GetStartupDirectory(), ShortcutName); }
        }

        private static string LegacyShortcutPath
        {
            get { return BuildShortcutPath(GetStartupDirectory(), LegacyShortcutName); }
        }

        public static bool IsEnabled()
        {
            string path = ShortcutPath;
            string legacyPath = LegacyShortcutPath;
            return (path.Length > 0 && File.Exists(path)) ||
                (legacyPath.Length > 0 && File.Exists(legacyPath));
        }

        public static bool MigrateLegacyShortcut()
        {
            return MigrateLegacyShortcut(GetStartupDirectory());
        }

        public static bool MigrateLegacyShortcut(string startupDirectory)
        {
            if (string.IsNullOrWhiteSpace(startupDirectory))
            {
                return false;
            }

            string legacyPath = BuildShortcutPath(startupDirectory, LegacyShortcutName);
            if (!File.Exists(legacyPath))
            {
                return false;
            }

            string path = BuildShortcutPath(startupDirectory, ShortcutName);
            if (File.Exists(path))
            {
                DeleteFile(legacyPath);
                return true;
            }

            string error;
            return SetEnabled(true, out error, startupDirectory);
        }

        public static bool SetEnabled(bool enabled, out string error)
        {
            return SetEnabled(enabled, out error, GetStartupDirectory());
        }

        private static bool SetEnabled(bool enabled, out string error, string startupDirectory)
        {
            error = string.Empty;
            string path = BuildShortcutPath(startupDirectory, ShortcutName);
            if (path.Length == 0)
            {
                error = "找不到当前用户的启动文件夹。";
                return false;
            }

            try
            {
                if (!enabled)
                {
                    DeleteFile(path);
                    DeleteFile(BuildShortcutPath(startupDirectory, LegacyShortcutName));
                    return true;
                }

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    error = "系统不支持创建快捷方式（WScript.Shell 不可用）。";
                    return false;
                }

                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(path);
                shortcut.TargetPath = Application.ExecutablePath;
                shortcut.Arguments = "--hidden";
                shortcut.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) ?? string.Empty;
                shortcut.Description = ShortcutDescription;
                shortcut.Save();
                DeleteFile(BuildShortcutPath(startupDirectory, LegacyShortcutName));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string GetStartupDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        private static string BuildShortcutPath(string startupDirectory, string fileName)
        {
            return string.IsNullOrWhiteSpace(startupDirectory)
                ? string.Empty
                : Path.Combine(startupDirectory, fileName);
        }

        private static void DeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
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
