using System;
using System.Collections.Generic;
using System.Linq;

namespace Valence.Viewer;

/// <summary>
/// Layer = length of the longest dependency chain below an item, after collapsing cycles
/// (Tarjan's strongly connected components). Items in a cycle share a layer and are flagged.
/// Used for systems in the scene and for classes inside an opened atom.
/// </summary>
public static class DependencyLayers
{
    public static (Dictionary<T, int> Layers, HashSet<T> InCycle) Compute<T>(
        IReadOnlyList<T> items, Func<T, IEnumerable<T>> dependsOn) where T : notnull
    {
        var members = new HashSet<T>(items);
        var deps = items.ToDictionary(
            item => item,
            item => dependsOn(item).Where(d => members.Contains(d) && !EqualityComparer<T>.Default.Equals(d, item)).Distinct().ToList());

        var index = new Dictionary<T, int>();
        var low = new Dictionary<T, int>();
        var onStack = new HashSet<T>();
        var stack = new Stack<T>();
        var component = new Dictionary<T, int>();
        var sizes = new List<int>();
        var next = 0;

        void Connect(T v)
        {
            index[v] = low[v] = next++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in deps[v])
            {
                if (!index.ContainsKey(w))
                {
                    Connect(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], index[w]);
                }
            }
            if (low[v] != index[v]) return;

            var id = sizes.Count;
            var size = 0;
            T member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component[member] = id;
                size++;
            } while (!EqualityComparer<T>.Default.Equals(member, v));
            sizes.Add(size);
        }

        foreach (var item in items)
        {
            if (!index.ContainsKey(item)) Connect(item);
        }

        var componentDeps = new HashSet<int>[sizes.Count];
        for (var c = 0; c < componentDeps.Length; c++) componentDeps[c] = new HashSet<int>();
        foreach (var item in items)
        {
            foreach (var dep in deps[item])
            {
                if (component[dep] != component[item]) componentDeps[component[item]].Add(component[dep]);
            }
        }

        var rank = Enumerable.Repeat(-1, sizes.Count).ToArray();
        int Rank(int c)
        {
            if (rank[c] >= 0) return rank[c];
            var r = 0;
            foreach (var dep in componentDeps[c]) r = Math.Max(r, Rank(dep) + 1);
            rank[c] = r;
            return r;
        }

        var layers = new Dictionary<T, int>();
        var inCycle = new HashSet<T>();
        foreach (var item in items)
        {
            layers[item] = Rank(component[item]);
            if (sizes[component[item]] > 1) inCycle.Add(item);
        }
        return (layers, inCycle);
    }
}
