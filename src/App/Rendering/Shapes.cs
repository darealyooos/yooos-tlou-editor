using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FactionsReborn.WorldEditor.Rendering;

/// <summary>Procedural helper geometry (gizmos, spawn markers, boundary walls, grid, selection box).</summary>
public static class Shapes
{
    public static Material Emissive(Color c)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(20, 20, 20))));
        g.Children.Add(new EmissiveMaterial(new SolidColorBrush(c)));
        g.Freeze();
        return g;
    }

    /// <summary>Emissive only: added on top of what is behind it (see-through walls).</summary>
    public static Material Additive(Color c)
    {
        var m = new EmissiveMaterial(new SolidColorBrush(c));
        m.Freeze();
        return m;
    }

    public static Material Diffuse(Color c)
    {
        var m = new DiffuseMaterial(new SolidColorBrush(c));
        m.Freeze();
        return m;
    }

    public static GeometryModel3D Model(MeshGeometry3D mesh, Material m)
    {
        mesh.Freeze();
        return new GeometryModel3D(mesh, m) { BackMaterial = m };
    }

    /// <summary>Axis-aligned box from min to max.</summary>
    public static MeshGeometry3D Box(Point3D min, Point3D max, MeshGeometry3D? into = null)
    {
        var mesh = into ?? new MeshGeometry3D();
        int b = mesh.Positions.Count;
        for (int i = 0; i < 8; i++)
            mesh.Positions.Add(new Point3D((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
        int[] faces = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
        foreach (var f in faces) mesh.TriangleIndices.Add(b + f);
        return mesh;
    }

    /// <summary>Thin box between two points (used as a line with thickness).</summary>
    public static void Segment(MeshGeometry3D mesh, Point3D a, Point3D b, double thickness)
    {
        var d = b - a;
        double len = d.Length;
        if (len < 1e-9) return;
        d /= len;
        var side = Vector3D.CrossProduct(d, Math.Abs(d.Y) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0));
        side.Normalize();
        var up = Vector3D.CrossProduct(side, d);
        side *= thickness / 2; up *= thickness / 2;
        int i0 = mesh.Positions.Count;
        foreach (var p in new[] { a, b })
        {
            mesh.Positions.Add(p - side - up); mesh.Positions.Add(p + side - up);
            mesh.Positions.Add(p + side + up); mesh.Positions.Add(p - side + up);
        }
        int[] q = [0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7, 0, 3, 2, 0, 2, 1, 4, 5, 6, 4, 6, 7];
        foreach (var k in q) mesh.TriangleIndices.Add(i0 + k);
    }

    /// <summary>The 12 edges of a box as thin segments.</summary>
    public static MeshGeometry3D WireBox(Point3D min, Point3D max, double thickness)
    {
        var mesh = new MeshGeometry3D();
        Point3D P(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        int[][] edges = [[0, 1], [2, 3], [4, 5], [6, 7], [0, 2], [1, 3], [4, 6], [5, 7], [0, 4], [1, 5], [2, 6], [3, 7]];
        foreach (var e in edges) Segment(mesh, P(e[0]), P(e[1]), thickness);
        return mesh;
    }

    /// <summary>Closed cone from a base centre to a tip (gizmo arrow heads).</summary>
    public static void Cone(MeshGeometry3D mesh, Point3D baseCentre, Point3D tip, double radius, int segments)
    {
        var d = tip - baseCentre;
        if (d.Length < 1e-9) return;
        d.Normalize();
        var u = Vector3D.CrossProduct(d, Math.Abs(d.Y) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0));
        u.Normalize();
        var v = Vector3D.CrossProduct(d, u);
        int b = mesh.Positions.Count;
        mesh.Positions.Add(tip);
        mesh.Positions.Add(baseCentre);
        for (int i = 0; i < segments; i++)
        {
            double a = 2 * Math.PI * i / segments;
            mesh.Positions.Add(baseCentre + (u * Math.Cos(a) + v * Math.Sin(a)) * radius);
        }
        for (int i = 0; i < segments; i++)
        {
            int p = b + 2 + i, q = b + 2 + (i + 1) % segments;
            mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(p); mesh.TriangleIndices.Add(q);       // side
            mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(q); mesh.TriangleIndices.Add(p);   // base
        }
    }

    /// <summary>UV sphere (gizmo centre).</summary>
    public static MeshGeometry3D Sphere(Point3D centre, double radius, int slices, int stacks)
    {
        var mesh = new MeshGeometry3D();
        for (int s = 0; s <= stacks; s++)
        {
            double phi = Math.PI * s / stacks;
            for (int i = 0; i <= slices; i++)
            {
                double th = 2 * Math.PI * i / slices;
                mesh.Positions.Add(new Point3D(centre.X + radius * Math.Sin(phi) * Math.Cos(th), centre.Y + radius * Math.Cos(phi), centre.Z + radius * Math.Sin(phi) * Math.Sin(th)));
            }
        }
        for (int s = 0; s < stacks; s++)
            for (int i = 0; i < slices; i++)
            {
                int a = s * (slices + 1) + i, b = a + slices + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1);
            }
        return mesh;
    }

    /// <summary>Lines every <paramref name="step"/> m, skipping those on multiples of <paramref name="skipEvery"/> (drawn by another layer).</summary>
    public static MeshGeometry3D Grid(double half, double step, double y, double thickness, double skipEvery)
    {
        var mesh = new MeshGeometry3D();
        for (double v = -half; v <= half + 1e-9; v += step)
        {
            if (skipEvery > 0 && Math.Abs(Math.IEEERemainder(v, skipEvery)) < 1e-6) continue;
            Segment(mesh, new Point3D(v, y, -half), new Point3D(v, y, half), thickness);
            Segment(mesh, new Point3D(-half, y, v), new Point3D(half, y, v), thickness);
        }
        return mesh;
    }

    public static MeshGeometry3D Grid(double half, double step, double y, double thickness)
    {
        var mesh = new MeshGeometry3D();
        for (double v = -half; v <= half + 1e-9; v += step)
        {
            Segment(mesh, new Point3D(v, y, -half), new Point3D(v, y, half), thickness);
            Segment(mesh, new Point3D(-half, y, v), new Point3D(half, y, v), thickness);
        }
        return mesh;
    }
}
