using System.Numerics;
using FactionsReborn.WorldEditor.Core.Pak;

namespace FactionsReborn.WorldEditor.Core.Scene;

public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    public Vector3 At(float t) => Origin + Direction * t;
}

public static class Raycast
{
    /// <summary>Slab test; returns entry distance or null.</summary>
    public static float? Aabb(Ray ray, Vector3 min, Vector3 max)
    {
        float tmin = 0, tmax = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float o = Get(ray.Origin, a), d = Get(ray.Direction, a), lo = Get(min, a), hi = Get(max, a);
            if (MathF.Abs(d) < 1e-9f) { if (o < lo || o > hi) return null; continue; }
            float t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1); tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return null;
        }
        return tmin;
    }

    /// <summary>Möller–Trumbore, both faces.</summary>
    public static float? Triangle(Ray ray, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(ray.Direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return null;
        float inv = 1 / det;
        var s = ray.Origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return null;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(ray.Direction, q) * inv;
        if (v < 0 || u + v > 1) return null;
        float t = Vector3.Dot(e2, q) * inv;
        return t > 1e-4f ? t : null;
    }

    public static float? Plane(Ray ray, float y)
    {
        if (MathF.Abs(ray.Direction.Y) < 1e-6f) return null;
        float t = (y - ray.Origin.Y) / ray.Direction.Y;
        return t > 0 ? t : null;
    }

    /// <summary>Nearest hit of a ray against the LOD0 triangles of placed instances (world space).</summary>
    public static float? Instances(Ray ray, GeometryData geo, IEnumerable<(int Prototype, Matrix4x4 World)> instances, float maxDistance = float.MaxValue)
    {
        float best = maxDistance;
        bool hit = false;
        foreach (var (proto, world) in instances)
        {
            var p = geo.Prototypes[proto];
            if (!Matrix4x4.Invert(world, out var inv)) continue;
            // Local-space ray; t stays comparable because the direction is transformed, not renormalised.
            var local = new Ray(Vector3.Transform(ray.Origin, inv), Vector3.TransformNormal(ray.Direction, inv));
            var box = Aabb(local, p.BoundsMin - new Vector3(0.01f), p.BoundsMax + new Vector3(0.01f));
            if (box is null || box.Value >= best) continue;
            foreach (int s in p.Lod0Submeshes)
            {
                var m = geo.Submeshes[s];
                var idx = m.Indices; var pos = m.Positions;
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    var t = Triangle(local, pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]]);
                    if (t is { } tt && tt < best) { best = tt; hit = true; }
                }
            }
        }
        return hit ? best : null;
    }

    private static float Get(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
}
