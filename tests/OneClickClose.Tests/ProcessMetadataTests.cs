using System.Diagnostics;
using System.IO;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class ProcessMetadataTests
    {
        [Fact]
        public void ToolhelpContainsCurrentProcessPathAndParent()
        {
            int currentId;
            string expectedPath;
            using (Process current = Process.GetCurrentProcess())
            {
                currentId = current.Id;
                expectedPath = current.MainModule.FileName;
            }

            ProcessMetadata metadata = ProcessMetadataProvider.Get();

            Assert.True(metadata.ParentMap.ContainsKey(currentId));
            Assert.True(metadata.PathMap.ContainsKey(currentId));
            Assert.Equal(
                Path.GetFullPath(expectedPath),
                Path.GetFullPath(metadata.PathMap[currentId]));
        }

        [Fact]
        public void CurrentProcessElevationMatchesTokenQuery()
        {
            bool expected = ProcessPrivilege.IsCurrentProcessElevated();
            using (Process current = Process.GetCurrentProcess())
            {
                Assert.Equal(expected, ProcessPrivilege.IsProcessElevated(current.Id));
            }
        }
    }
}
