using System.Numerics;
using FactionsReborn.WorldEditor.Core.Pak;

namespace FactionsReborn.WorldEditor.Core.Scene;

/// <summary>
/// World-space bounds of a level's placed instances, used to frame the camera.
/// Uses the decoded LOD0 submesh bounds, not the prototype header bounds: some prototypes (helpers, markers,
/// geometry-less entries) store sentinel values around ±1e38 there, which sent the camera to ~1e43 m.
/// </summary>
public static class LevelBounds
{
    /// <summary>Coordinates beyond this (metres) are treated as invalid data.</summary>
    public const float MaxCoordinate = 100_000f;

    public static (Vector3 Min, Vector3 Max) Compute(GeometryData geo, IEnumerable<(int Prototype, Matrix4x4 World)> instances)
    {
        // Local bounds per prototype from its decoded LOD0 submeshes (null = no usable geometry).
        var local = new (Vector3 Min, Vector3 Max)?[geo.Prototypes.Count];
        for (int i = 0; i < local.Length; i++)
        {
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            bool any = false;
            foreach (int s in geo.Prototypes[i].Lod0Submeshes)
            {
                if (s < 0 || s >= geo.Submeshes.Count) continue;
                var m = geo.Submeshes[s];
                if (m.Indices.Length == 0 || !Valid(m.BoundsMin) || !Valid(m.BoundsMax)) continue;
                lo = Vector3.Min(lo, m.BoundsMin); hi = Vector3.Max(hi, m.BoundsMax);
                any = true;
            }
            local[i] = any ? (lo, hi) : null;
        }

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        bool found = false;
        foreach (var (proto, world) in instances)
        {
            if (proto < 0 || proto >= local.Length || local[proto] is not { } b) continue;
            Vector3 imin = new(float.MaxValue), imax = new(float.MinValue);
            bool ok = true;
            for (int c = 0; c < 8 && ok; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? b.Min.X : b.Max.X, (c & 2) == 0 ? b.Min.Y : b.Max.Y, (c & 4) == 0 ? b.Min.Z : b.Max.Z);
                var w = Vector3.Transform(corner, world);
                ok = Valid(w);
                imin = Vector3.Min(imin, w); imax = Vector3.Max(imax, w);
            }
            if (!ok) continue; // a broken instance matrix must not blow up the whole map
            min = Vector3.Min(min, imin); max = Vector3.Max(max, imax);
            found = true;
        }
        return found ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }

    private static bool Valid(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
        && MathF.Abs(v.X) <= MaxCoordinate && MathF.Abs(v.Y) <= MaxCoordinate && MathF.Abs(v.Z) <= MaxCoordinate;
}
