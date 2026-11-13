using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class ConfigStoreTests
    {
        private const string LegacyJson = @"{
  ""hotkey"": ""Ctrl+Shift+Z"",
  ""gracefulTimeoutSeconds"": 9,
  ""protectedProcesses"": [""legacy-alpha"", ""legacy-beta""],
  ""lastMode"": ""all""
}";

        [Fact]
        public void LegacyConfigIsPreservedAndMigratedOnSave()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string path = Path.Combine(temp.Path, "config.json");
                File.WriteAllText(path, LegacyJson, new UTF8Encoding(true));

                ConfigStore store = new ConfigStore(path);
                store.Load();

                Assert.Equal(string.Empty, store.LastLoadError);
                Assert.Equal("Ctrl+Shift+Z", store.Current.Hotkey);
                Assert.Equal(9, store.Current.GracefulTimeoutSeconds);
                Assert.Equal("all", store.Current.LastMode);
                Assert.Equal(
                    new[] { "legacy-alpha", "legacy-beta" },
                    store.Current.ProtectedProcesses.ToArray());
                Assert.Empty(store.Current.Profiles);

                Assert.True(store.Save());

                using (JsonDocument current = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    Assert.Equal(2, current.RootElement.GetProperty("schemaVersion").GetInt32());
                    Assert.False(current.RootElement.GetProperty("profiles").TryGetProperty("工作", out _));
                }

                string backupPath = path + ".bak";
                Assert.True(File.Exists(backupPath));
                using (JsonDocument backup = JsonDocument.Parse(File.ReadAllText(backupPath)))
                {
                    Assert.False(backup.RootElement.TryGetProperty("schemaVersion", out _));
                    Assert.Equal("Ctrl+Shift+Z", backup.RootElement.GetProperty("hotkey").GetString());
                    Assert.Equal(
                        new[] { "legacy-alpha", "legacy-beta" },
                        backup.RootElement.GetProperty("protectedProcesses")
                            .EnumerateArray()
                            .Select(item => item.GetString())
                            .ToArray());
                }
            }
        }

        [Fact]
        public void MissingConfigCreatesDefaultSchemaVersionTwo()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string path = Path.Combine(temp.Path, "config.json");
                ConfigStore store = new ConfigStore(path);
                store.Load();

                Assert.True(store.LastSaveSucceeded);
                Assert.True(File.Exists(path));
                ConfigStore reloadedStore = new ConfigStore(path);
                reloadedStore.Load();
                AppConfig reloaded = reloadedStore.Current;
                Assert.Equal(2, reloaded.SchemaVersion);
                Assert.Equal("Ctrl+Alt+X", reloaded.Hotkey);
            }
        }

        [Fact]
        public void ProfilesCanBeSavedAppliedAndDeleted()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string path = Path.Combine(temp.Path, "config.json");
                ConfigStore store = new ConfigStore(path);
                store.Load();

                Assert.True(store.SaveProfile("工作", new[] { "alpha", "beta" }));
                Assert.Equal("工作", store.Current.ActiveProfile);
                Assert.True(store.Current.Profiles.ContainsKey("工作"));
                Assert.Equal(
                    new[] { "alpha", "beta" },
                    store.Current.Profiles["工作"].ToArray());

                store.Current.ProtectedProcesses = new List<string> { "temporary" };
                Assert.True(store.ApplyProfile("工作"));
                Assert.Equal(
                    new[] { "alpha", "beta" },
                    store.Current.ProtectedProcesses.ToArray());
                Assert.Equal("工作", store.Current.ActiveProfile);

                Assert.True(store.DeleteProfile("工作"));
                Assert.False(store.Current.Profiles.ContainsKey("工作"));
                Assert.Empty(store.Current.ActiveProfile);
                Assert.Equal(
                    new[] { "alpha", "beta" },
                    store.Current.ProtectedProcesses.ToArray());

                ConfigStore reloaded = new ConfigStore(path);
                reloaded.Load();
                Assert.False(reloaded.Current.Profiles.ContainsKey("工作"));
            }
        }

        [Fact]
        public void RestoreBackupRestoresPreviousConfigAndCreatesRestorePoint()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string path = Path.Combine(temp.Path, "config.json");
                File.WriteAllText(path, LegacyJson, new UTF8Encoding(true));

                ConfigStore store = new ConfigStore(path);
                store.Load();
                Assert.True(store.SaveProfile("工作", new[] { "legacy-alpha", "legacy-beta" }));
                Assert.True(store.SetProtection(new[] { "newer-state" }, true) > 0);

                Assert.True(store.RestoreBackup());
                Assert.Equal(
                    new[] { "legacy-alpha", "legacy-beta" },
                    store.Current.ProtectedProcesses.ToArray());
                Assert.True(store.Current.Profiles.ContainsKey("工作"));
                Assert.False(string.IsNullOrEmpty(store.LastRestorePointPath));
                Assert.True(File.Exists(store.LastRestorePointPath));

                string restorePoint = File.ReadAllText(store.LastRestorePointPath);
                Assert.Contains("newer-state", restorePoint);
            }
        }

        [Fact]
        public void SaveFailureRollsBackProtectionChanges()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string blockedParent = Path.Combine(temp.Path, "blocked-parent");
                File.WriteAllText(blockedParent, "this is a file, not a directory");

                ConfigStore store = new ConfigStore(Path.Combine(blockedParent, "config.json"));
                store.Load();
                Assert.False(store.LastSaveSucceeded);

                int count = store.SetProtection(new[] { "alpha" }, true);
                Assert.Equal(0, count);
                Assert.False(store.LastSaveSucceeded);
                Assert.Empty(store.Current.ProtectedProcesses);
            }
        }

        [Fact]
        public void LegacyConfigUsesDefaultsForNewViewFields()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string path = Path.Combine(temp.Path, "config.json");
                File.WriteAllText(path, LegacyJson, new UTF8Encoding(true));

                ConfigStore store = new ConfigStore(path);
                store.Load();

                Assert.True(store.Current.IncludeChildProcesses);
                Assert.False(store.Current.ShowAllWindows);
                Assert.False(store.Current.IncludeSuspendedUwp);
                Assert.Empty(store.Current.ProtectedPaths);
            }
        }

        [Fact]
        public void ViewPreferencesAndPathProtectionRoundTrip()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string path = Path.Combine(temp.Path, "config.json");
                string exePath = Path.Combine(temp.Path, "apps", "demo.exe");
                ConfigStore store = new ConfigStore(path);
                store.Load();

                store.Current.ShowAllWindows = true;
                store.Current.IncludeChildProcesses = false;
                store.Current.IncludeSuspendedUwp = true;
                Assert.True(store.SetPathProtection(new[] { exePath, exePath }, true) > 0);

                ConfigStore reloaded = new ConfigStore(path);
                reloaded.Load();
                Assert.True(reloaded.Current.ShowAllWindows);
                Assert.False(reloaded.Current.IncludeChildProcesses);
                Assert.True(reloaded.Current.IncludeSuspendedUwp);
                Assert.Single(reloaded.Current.ProtectedPaths);
                Assert.Equal(
                    ProcessNameUtility.NormalizePath(exePath),
                    reloaded.Current.ProtectedPaths[0]);

                Assert.True(reloaded.SetPathProtection(new[] { exePath }, false) > 0);
                Assert.Empty(reloaded.Current.ProtectedPaths);
            }
        }

        [Fact]
        public void QuietSaveDoesNotRotateBackup()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string path = Path.Combine(temp.Path, "config.json");
                ConfigStore store = new ConfigStore(path);
                store.Load();

                store.Current.ProtectedProcesses = new List<string> { "keep-me" };
                Assert.True(store.Save());
                Assert.True(store.Save());
                string backupPath = path + ".bak";
                Assert.True(File.Exists(backupPath));
                string backupBefore = File.ReadAllText(backupPath);

                store.Current.ShowAllWindows = true;
                Assert.True(store.Save(false));

                Assert.Equal(backupBefore, File.ReadAllText(backupPath));
                using (JsonDocument current = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    Assert.True(current.RootElement.GetProperty("showAllWindows").GetBoolean());
                }
            }
        }

        [Fact]
        public void SuccessfulSaveClearsPreviousLoadError()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string path = Path.Combine(temp.Path, "config.json");
                File.WriteAllText(path, "{ not valid json", new UTF8Encoding(true));

                ConfigStore store = new ConfigStore(path);
                store.Load();
                Assert.False(string.IsNullOrEmpty(store.LastLoadError));

                store.Current.ShowAllWindows = true;
                Assert.True(store.Save());
                Assert.Equal(string.Empty, store.LastLoadError);

                ConfigStore reloaded = new ConfigStore(path);
                reloaded.Load();
                Assert.True(reloaded.Current.ShowAllWindows);
            }
        }
    }
}
