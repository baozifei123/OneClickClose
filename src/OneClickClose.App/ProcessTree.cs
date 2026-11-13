using System;
using System.Collections.Generic;
using System.Linq;

namespace OneClickClose
{
    public static class ProcessTree
    {
        public static HashSet<int> BuildAncestorSet(int startId, IDictionary<int, int> parentMap)
        {
            HashSet<int> set = new HashSet<int>();
            int current = startId;
            int guard = 0;
            while (current > 0 && guard < 64)
            {
                if (!set.Add(current))
                {
                    break;
                }
                int parent;
                if (parentMap == null || !parentMap.TryGetValue(current, out parent))
                {
                    break;
                }
                current = parent;
                guard++;
            }
            return set;
        }

        public static HashSet<int> GetDescendantIds(
            IEnumerable<int> rootIds,
            IDictionary<int, int> parentMap)
        {
            HashSet<int> descendants = new HashSet<int>();
            if (rootIds == null || parentMap == null)
            {
                return descendants;
            }

            HashSet<int> roots = new HashSet<int>(rootIds.Where(id => id > 0));
            if (roots.Count == 0)
            {
                return descendants;
            }

            Dictionary<int, List<int>> children = new Dictionary<int, List<int>>();
            foreach (KeyValuePair<int, int> pair in parentMap)
            {
                if (pair.Key <= 0 || pair.Value <= 0 || pair.Key == pair.Value)
                {
                    continue;
                }
                List<int> list;
                if (!children.TryGetValue(pair.Value, out list))
                {
                    list = new List<int>();
                    children[pair.Value] = list;
                }
                list.Add(pair.Key);
            }

            Queue<int> queue = new Queue<int>(roots);
            HashSet<int> visited = new HashSet<int>(roots);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                List<int> childIds;
                if (!children.TryGetValue(current, out childIds))
                {
                    continue;
                }
                foreach (int childId in childIds)
                {
                    if (!visited.Add(childId))
                    {
                        continue;
                    }
                    if (!roots.Contains(childId))
                    {
                        descendants.Add(childId);
                    }
                    queue.Enqueue(childId);
                }
            }
            return descendants;
        }

        public static bool IsDescendantOf(
            int processId,
            int rootId,
            IDictionary<int, int> parentMap,
            int maxDepth = 64)
        {
            if (processId <= 0 || rootId <= 0 || processId == rootId || parentMap == null)
            {
                return false;
            }

            int current = processId;
            int guard = 0;
            while (current > 0 && guard < maxDepth)
            {
                int parent;
                if (!parentMap.TryGetValue(current, out parent))
                {
                    return false;
                }
                if (parent == rootId)
                {
                    return true;
                }
                if (parent == current)
                {
                    return false;
                }
                current = parent;
                guard++;
            }
            return false;
        }
    }
}
