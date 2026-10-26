using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// Deterministic layout:
///  - height (Y) is the dependency layer, so "up" always means "depends on what's below";
///  - X/Z come from a force simulation: bonded systems attract, everything repels,
///    systems in the same molecule (project) cluster together;
///  - pinned systems (dragged by the user) keep their X/Z.
/// </summary>
public static class Layout
{
    private const float GoldenAngle = 2.39996323f; // π(3 − √5)

    public static void Run(GraphModel model, IReadOnlyDictionary<string, Vector2> pins)
    {
        foreach (var atom in model.AtomList) PackNucleus(atom);
        SolveHorizontal(model, pins);
    }

    public static float LayerHeight(GraphModel model, int layer)
    {
        var spacing = model.MaxLayer <= 8 ? 6f : Mathf.Max(2.5f, 48f / model.MaxLayer);
        return (layer - model.MaxLayer / 2f) * spacing;
    }

    private static void PackNucleus(AtomData atom)
    {
        // Hash order mixes protons and neutrons the way a real nucleus would.
        var nodes = atom.Nodes.OrderBy(n => Palette.Hash(n.Info.Id)).ToList();
        var count = nodes.Count;
        var maxRadius = 0f;
        foreach (var node in nodes)
        {
            var loc = (float)node.Metric(MetricKeys.Loc);
            node.Radius = 0.16f + 0.07f * MathF.Log10(1f + loc);
            maxRadius = Mathf.Max(maxRadius, node.Radius);
        }

        var core = count <= 1 ? 0f : 0.30f * MathF.Cbrt(count) + 0.1f;
        for (var i = 0; i < count; i++)
        {
            if (count == 1)
            {
                nodes[i].LocalOffset = Vector3.Zero;
                continue;
            }
            var radius = core * MathF.Cbrt((i + 0.5f) / count);
            nodes[i].LocalOffset = Fibonacci(i, count) * radius;
        }

        atom.CoreRadius = core + maxRadius;
        atom.Radius = atom.CoreRadius + 1.1f;
    }

    private static Vector3 Fibonacci(int i, int count)
    {
        var y = 1f - 2f * (i + 0.5f) / count;
        var r = MathF.Sqrt(Mathf.Max(0f, 1f - y * y));
        var theta = GoldenAngle * i;
        return new Vector3(MathF.Cos(theta) * r, y, MathF.Sin(theta) * r);
    }

    private static void SolveHorizontal(GraphModel model, IReadOnlyDictionary<string, Vector2> pins)
    {
        var atoms = model.AtomList;
        var n = atoms.Count;
        if (n == 0) return;

        var indexOf = new Dictionary<AtomData, int>();
        for (var i = 0; i < n; i++) indexOf[atoms[i]] = i;

        var position = new Vector2[n];
        var displacement = new Vector2[n];
        var radius = atoms.Select(a => a.Radius).ToArray();
        var pinned = new bool[n];
        var moleculeIndex = new int[n];

        var spread = MathF.Sqrt(n) * 3f + 4f;
        var molecules = model.Molecules;
        for (var m = 0; m < molecules.Count; m++)
        {
            var angle = Mathf.Tau * m / molecules.Count;
            var center = molecules.Count == 1
                ? Vector2.Zero
                : new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * spread * 0.6f;
            var members = molecules[m].Atoms;
            foreach (var atom in members)
            {
                var i = indexOf[atom];
                moleculeIndex[i] = m;
                var h = Palette.Hash01(atom.Id);
                var h2 = Palette.Hash01(atom.Id + "#");
                position[i] = center + new Vector2(MathF.Cos(h * Mathf.Tau), MathF.Sin(h * Mathf.Tau))
                    * (1f + h2 * MathF.Sqrt(members.Count) * 2f);
            }
        }

        for (var i = 0; i < n; i++)
        {
            if (pins.TryGetValue(atoms[i].Id, out var pin))
            {
                position[i] = pin;
                pinned[i] = true;
            }
            atoms[i].Pinned = pinned[i];
        }

        var bonds = model.Bonds
            .Select(b => (A: indexOf[b.A], B: indexOf[b.B], Strength: 0.5f + 0.25f * MathF.Log(1f + b.Weight)))
            .ToArray();

        var centroids = new Vector2[molecules.Count];
        var counts = new int[molecules.Count];
        var iterations = (int)Math.Clamp(4e7 / Math.Max(1.0, (double)n * n), 60, 400);
        var temperature = spread * 0.15f;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(displacement);

            // Repulsion (Fruchterman–Reingold), weaker between different layers since they're
            // separated vertically anyway, plus a hard push when bubbles overlap.
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var delta = position[i] - position[j];
                    var distanceSquared = delta.LengthSquared();
                    if (distanceSquared < 1e-4f)
                    {
                        delta = new Vector2(0.01f * (i - j), 0.01f);
                        distanceSquared = delta.LengthSquared();
                    }
                    var distance = MathF.Sqrt(distanceSquared);
                    var direction = delta / distance;
                    var layerGap = Math.Abs(atoms[i].Layer - atoms[j].Layer);
                    var k = radius[i] + radius[j] + 2f;
                    var force = k * k / distance / (1f + layerGap);
                    var minimum = radius[i] + radius[j] + 1.2f;
                    if (distance < minimum && layerGap <= 1) force += (minimum - distance) * 4f;
                    displacement[i] += direction * force;
                    displacement[j] -= direction * force;
                }
            }

            foreach (var (a, b, strength) in bonds)
            {
                var delta = position[b] - position[a];
                var distance = Mathf.Max(delta.Length(), 0.01f);
                var k = radius[a] + radius[b] + 2f;
                var force = distance * distance / k * strength;
                var direction = delta / distance;
                displacement[a] += direction * force;
                displacement[b] -= direction * force;
            }

            Array.Clear(centroids);
            Array.Clear(counts);
            for (var i = 0; i < n; i++)
            {
                centroids[moleculeIndex[i]] += position[i];
                counts[moleculeIndex[i]]++;
            }
            for (var i = 0; i < n; i++)
            {
                var m = moleculeIndex[i];
                if (counts[m] > 1) displacement[i] += (centroids[m] / counts[m] - position[i]) * 0.08f * (radius[i] + 2f);
                displacement[i] -= position[i] * 0.01f;
            }

            for (var i = 0; i < n; i++)
            {
                if (pinned[i]) continue;
                var length = displacement[i].Length();
                if (length > 1e-6f) position[i] += displacement[i] / length * Mathf.Min(length, temperature);
            }
            temperature = Mathf.Max(0.02f, temperature * 0.985f);
        }

        for (var i = 0; i < n; i++)
        {
            atoms[i].Position = new Vector3(position[i].X, LayerHeight(model, atoms[i].Layer), position[i].Y);
        }
    }
}
