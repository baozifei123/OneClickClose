using System.IO;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class StartupManagerTests
    {
        [Fact]
        public void ExistingNewShortcutWinsAndLegacyIsDeleted()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                string legacy = Path.Combine(temp.Path, "一键收摊.lnk");
                string current = Path.Combine(temp.Path, "清场助手.lnk");
                File.WriteAllText(legacy, "legacy");
                File.WriteAllText(current, "current");

                Assert.True(StartupManager.MigrateLegacyShortcut(temp.Path));
                Assert.False(File.Exists(legacy));
                Assert.True(File.Exists(current));
                Assert.Equal("current", File.ReadAllText(current));
            }
        }

        [Fact]
        public void LegacyShortcutIsConvertedToNewShortcut()
        {
            StaRunner.Run(delegate
            {
                using (TempDirectory temp = new TempDirectory())
                {
                    string legacy = Path.Combine(temp.Path, "一键收摊.lnk");
                    string current = Path.Combine(temp.Path, "清场助手.lnk");
                    File.WriteAllText(legacy, "legacy");

                    Assert.True(StartupManager.MigrateLegacyShortcut(temp.Path));
                    Assert.False(File.Exists(legacy));
                    Assert.True(File.Exists(current));
                    Assert.True(new FileInfo(current).Length > 0);
                }
            });
        }

        [Fact]
        public void MissingLegacyShortcutIsNoOp()
        {
            using (TempDirectory temp = new TempDirectory())
            {
                Assert.False(StartupManager.MigrateLegacyShortcut(temp.Path));
                Assert.False(File.Exists(Path.Combine(temp.Path, "清场助手.lnk")));
            }
        }
    }
}
