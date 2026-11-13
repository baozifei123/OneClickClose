using Xunit;

namespace OneClickClose.Tests
{
    public sealed class CandidateFilterTests
    {
        private static AppCandidate CreateApp(bool taskbarApp, string displayName, string name, string titles)
        {
            return new AppCandidate
            {
                DisplayName = displayName,
                Name = name,
                Titles = titles,
                IsTaskbarApp = taskbarApp
            };
        }

        [Fact]
        public void HidesNonTaskbarAppsUnlessShowAllIsChecked()
        {
            AppCandidate app = CreateApp(false, "ASUS Monitor Control", "AsMonitorControl", "AsHDRControl");

            Assert.False(CandidateFilter.Matches(app, string.Empty, false));
            Assert.True(CandidateFilter.Matches(app, string.Empty, true));
        }

        [Fact]
        public void SearchMatchesDisplayNameNameOrWindowTitle()
        {
            AppCandidate app = CreateApp(true, "计算器", "calc", "Calculator");

            Assert.True(CandidateFilter.Matches(app, "计算器", false));
            Assert.True(CandidateFilter.Matches(app, "calc", false));
            Assert.True(CandidateFilter.Matches(app, "Calculator", false));
            Assert.False(CandidateFilter.Matches(app, "notepad", false));
        }

        [Fact]
        public void SuspendedUwpIsHiddenUnlessExplicitlyIncluded()
        {
            AppCandidate settings = CreateApp(false, "设置", "SystemSettings", "设置");
            settings.IsSuspendedUwp = true;

            Assert.False(CandidateFilter.Matches(settings, string.Empty, false, false));
            Assert.True(CandidateFilter.Matches(settings, string.Empty, false, true));
            Assert.True(CandidateFilter.Matches(settings, string.Empty, true, false));
        }
    }
}
