using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class ProcessTreeTests
    {
        private static Dictionary<int, int> CreateParentMap()
        {
            return new Dictionary<int, int>
            {
                { 1, 0 },
                { 2, 1 },
                { 3, 2 },
                { 4, 1 },
                { 5, 4 },
                { 6, 0 },
                { 7, 8 },
                { 8, 7 }
            };
        }

        [Fact]
        public void DescendantsIncludeAllLevelsButNotRoots()
        {
            HashSet<int> descendants = ProcessTree.GetDescendantIds(
                new[] { 1 },
                CreateParentMap());

            Assert.Equal(new[] { 2, 3, 4, 5 }, descendants.OrderBy(id => id).ToArray());
            Assert.DoesNotContain(1, descendants);
            Assert.DoesNotContain(6, descendants);
        }

        [Fact]
        public void AncestorCheckStopsAtRootAndHandlesCycles()
        {
            Dictionary<int, int> parentMap = CreateParentMap();

            Assert.True(ProcessTree.IsDescendantOf(5, 1, parentMap));
            Assert.True(ProcessTree.IsDescendantOf(2, 1, parentMap));
            Assert.False(ProcessTree.IsDescendantOf(6, 1, parentMap));
            Assert.False(ProcessTree.IsDescendantOf(7, 7, parentMap));
            Assert.False(ProcessTree.IsDescendantOf(7, 1, parentMap));
        }

        [Fact]
        public void AncestorSetIncludesSelfAndStopsAtMissingParent()
        {
            HashSet<int> ancestors = ProcessTree.BuildAncestorSet(5, CreateParentMap());

            Assert.Equal(new[] { 1, 4, 5 }, ancestors.OrderBy(id => id).ToArray());
        }
    }
}
