using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class ProcessGroupingTests
    {
        private static WindowInfo CreateWindow(int processId, string title)
        {
            return new WindowInfo
            {
                Handle = new IntPtr(processId),
                Title = title,
                ProcessId = (uint)processId
            };
        }

        private static AppCandidate CreateApp(
            int processId,
            string name,
            string displayName,
            string path,
            bool protectedFlag,
            bool foreground,
            params WindowInfo[] windows)
        {
            return new AppCandidate
            {
                ProcessId = processId,
                ProcessIds = new[] { processId },
                Name = name,
                DisplayName = displayName,
                Path = path,
                Windows = new List<WindowInfo>(windows),
                Kind = windows.Length > 0 ? (foreground ? "前台" : "窗口") : "后台",
                Titles = string.Join(" / ", windows.Select(window => window.Title).ToArray()),
                Protected = protectedFlag,
                IsForeground = foreground,
                IsTaskbarApp = windows.Any(window => window.IsTaskbarWindow)
            };
        }

        [Fact]
        public void GroupsByPathAndKeepsUnknownPathsSeparate()
        {
            AppCandidate a = CreateApp(
                10, "foo", "Foo", @"C:\Apps\foo.exe", false, true,
                CreateWindow(10, "A"));
            AppCandidate b = CreateApp(
                11, "foo", "Foo", @"C:\Apps\foo.exe", true, false,
                CreateWindow(11, "B"), CreateWindow(11, "B2"));
            AppCandidate c = CreateApp(12, "bar", "Bar", string.Empty, false, false);
            AppCandidate d = CreateApp(13, "bar", "Bar", string.Empty, false, false);

            List<AppCandidate> grouped = ProcessScanner.GroupCandidates(new[] { a, b, c, d });

            Assert.Equal(3, grouped.Count);
            AppCandidate foo = grouped.Single(candidate => candidate.Path == @"C:\Apps\foo.exe");
            Assert.Equal(new[] { 10, 11 }, foo.ProcessIds);
            Assert.Equal(3, foo.WindowCount);
            Assert.False(foo.Protected);
            Assert.True(foo.IsForeground);
            Assert.Equal("前台", foo.Kind);
            Assert.Contains("A", foo.Titles);
            Assert.Contains("B", foo.Titles);

            List<AppCandidate> unknown = grouped
                .Where(candidate => string.IsNullOrEmpty(candidate.Path))
                .ToList();
            Assert.Equal(2, unknown.Count);
            Assert.All(unknown, candidate => Assert.Equal("bar", candidate.Name));
        }

        [Fact]
        public void GroupIsProtectedOnlyWhenAllMembersAreProtected()
        {
            AppCandidate a = CreateApp(
                20, "foo", "Foo", @"C:\Apps\foo.exe", true, false,
                CreateWindow(20, "A"));
            AppCandidate b = CreateApp(
                21, "foo", "Foo", @"C:\Apps\foo.exe", true, false,
                CreateWindow(21, "B"));

            AppCandidate group = Assert.Single(ProcessScanner.GroupCandidates(new[] { a, b }));
            Assert.True(group.Protected);
            Assert.False(group.IsForeground);
            Assert.Equal("窗口", group.Kind);
        }

        [Fact]
        public void GroupIsTaskbarAppWhenAnyMemberHasTaskbarWindow()
        {
            WindowInfo taskbarWindow = CreateWindow(40, "A");
            taskbarWindow.IsTaskbarWindow = true;
            AppCandidate a = CreateApp(
                40, "foo", "Foo", @"C:\Apps\foo.exe", false, false, taskbarWindow);
            AppCandidate b = CreateApp(
                41, "foo", "Foo", @"C:\Apps\foo.exe", false, false,
                CreateWindow(41, "B"));

            AppCandidate group = Assert.Single(ProcessScanner.GroupCandidates(new[] { a, b }));
            Assert.True(group.IsTaskbarApp);

            AppCandidate backgroundOnly = CreateApp(
                42, "bar", "Bar", @"C:\Apps\bar.exe", false, false,
                CreateWindow(42, "C"));
            AppCandidate noTaskbar = Assert.Single(ProcessScanner.GroupCandidates(new[] { backgroundOnly }));
            Assert.False(noTaskbar.IsTaskbarApp);
        }

        [Fact]
        public void GroupPropagatesSuspendedUwpAndElevationFlags()
        {
            AppCandidate a = CreateApp(
                50, "foo", "Foo", @"C:\Apps\foo.exe", false, false,
                CreateWindow(50, "A"));
            a.IsSuspendedUwp = true;
            a.RequiresElevation = true;
            AppCandidate b = CreateApp(
                51, "foo", "Foo", @"C:\Apps\foo.exe", false, false,
                CreateWindow(51, "B"));

            AppCandidate group = Assert.Single(ProcessScanner.GroupCandidates(new[] { a, b }));
            Assert.True(group.IsSuspendedUwp);
            Assert.True(group.RequiresElevation);
        }

        [Fact]
        public void SortPutsForegroundFirstAndProtectedLast()
        {
            AppCandidate protectedApp = CreateApp(
                30, "protected", "A Protected", @"C:\Apps\protected.exe", true, false,
                CreateWindow(30, "P"));
            AppCandidate normalApp = CreateApp(
                31, "normal", "B Normal", @"C:\Apps\normal.exe", false, false,
                CreateWindow(31, "N"));
            AppCandidate foregroundApp = CreateApp(
                32, "foreground", "C Foreground", @"C:\Apps\foreground.exe", false, true,
                CreateWindow(32, "F"));

            List<AppCandidate> grouped = ProcessScanner.GroupCandidates(
                new[] { protectedApp, normalApp, foregroundApp });

            Assert.Equal("C Foreground", grouped[0].DisplayName);
            Assert.Equal("B Normal", grouped[1].DisplayName);
            Assert.Equal("A Protected", grouped[2].DisplayName);
        }
    }
}
