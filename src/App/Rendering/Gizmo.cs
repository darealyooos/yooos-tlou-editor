using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FactionsReborn.WorldEditor.Rendering;

public enum GizmoMode { Move, Rotate, Scale }

/// <summary>
/// Move / rotate / scale handles drawn at the selection. Picking and dragging are done in screen space
/// against the projected handles, so the gizmo stays usable when it is inside map geometry.
/// Visuals: cone tips (move), cube tips and a uniform centre cube (scale), rings (rotate); the hovered axis is
/// drawn brighter and thicker.
/// </summary>
public sealed class Gizmo
{
    public static readonly Vector3D[] Axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
    private static readonly Color[] Colors = [Color.FromRgb(229, 83, 75), Color.FromRgb(108, 194, 74), Color.FromRgb(76, 139, 245)];
    private static readonly Color Hot = Color.FromRgb(255, 214, 92);
    private static readonly Color Centre = Color.FromRgb(214, 216, 222);

    public ModelVisual3D Visual { get; } = new();
    public GizmoMode Mode { get; set; } = GizmoMode.Move;
    public bool[] Enabled { get; } = [true, true, true];
    public Point3D Origin { get; private set; }
    public double Size { get; private set; } = 1;
    public int HotAxis { get; set; } = -1;
    public bool Visible { get; private set; }

    public void Hide() { Visible = false; Visual.Content = null; }

    public void Show(Point3D origin, CameraRig camera)
    {
        Visible = true;
        Origin = origin;
        // Constant screen size: 15 % of the distance in perspective (≈ 10.7 % of the view width at 70°), same share in ortho.
        Size = camera.Orthographic ? Math.Max(0.05, camera.Ortho.Width * 0.107) : Math.Max(0.25, (origin - camera.Position).Length * 0.15);
        var group = new Model3DGroup();
        for (int a = 0; a < 3; a++)
        {
            if (!Enabled[a]) continue;
            bool hot = a == HotAxis;
            var mesh = new MeshGeometry3D();
            var color = hot ? Hot : Colors[a];
            double t = Size * (hot ? 0.042 : 0.028);
            if (Mode == GizmoMode.Rotate)
            {
                var pts = Ring(a, 72);
                for (int i = 0; i < pts.Length; i++) Shapes.Segment(mesh, pts[i], pts[(i + 1) % pts.Length], t * 1.25);
            }
            else
            {
                var start = origin + Axes[a] * Size * 0.12;
                var end = origin + Axes[a] * Size;
                Shapes.Segment(mesh, start, end, t);
                if (Mode == GizmoMode.Scale)
                {
                    double h = Size * (hot ? 0.075 : 0.06);
                    Shapes.Box(new Point3D(end.X - h, end.Y - h, end.Z - h), new Point3D(end.X + h, end.Y + h, end.Z + h), mesh);
                }
                else
                {
                    Shapes.Cone(mesh, end - Axes[a] * Size * 0.02, end + Axes[a] * Size * (hot ? 0.24 : 0.2), Size * (hot ? 0.075 : 0.06), 20);
                }
            }
            group.Children.Add(Shapes.Model(mesh, Shapes.Emissive(color)));
        }
        if (Mode != GizmoMode.Rotate)
        {
            // Neutral origin marker (scale: a uniform cube, matching Shift-drag uniform scaling).
            double c = Size * (Mode == GizmoMode.Scale ? 0.07 : 0.045);
            var centre = Mode == GizmoMode.Scale
                ? Shapes.Box(new Point3D(origin.X - c, origin.Y - c, origin.Z - c), new Point3D(origin.X + c, origin.Y + c, origin.Z + c))
                : Shapes.Sphere(origin, c, 12, 8);
            group.Children.Add(Shapes.Model(centre, Shapes.Emissive(Centre)));
        }
        Visual.Content = group;
    }

    private Point3D[] Ring(int axis, int segments)
    {
        var u = Axes[(axis + 1) % 3]; var v = Axes[(axis + 2) % 3];
        var pts = new Point3D[segments];
        for (int i = 0; i < segments; i++)
        {
            double ang = 2 * Math.PI * i / segments;
            pts[i] = Origin + (u * Math.Cos(ang) + v * Math.Sin(ang)) * Size * 0.8;
        }
        return pts;
    }

    /// <summary>Axis under the mouse (within 9 px of its projected handle), or -1.</summary>
    public int Pick(Point mouse, CameraRig camera, Size viewport)
    {
        if (!Visible) return -1;
        int best = -1; double bestDist = 9;
        for (int a = 0; a < 3; a++)
        {
            if (!Enabled[a]) continue;
            var poly = Mode == GizmoMode.Rotate
                ? Ring(a, 48).Append(Ring(a, 48)[0]).ToArray()
                : [Origin + Axes[a] * Size * 0.15, Origin + Axes[a] * Size * 1.12];
            for (int i = 0; i + 1 < poly.Length; i++)
            {
                if (camera.Project(poly[i], viewport) is not { } p0 || camera.Project(poly[i + 1], viewport) is not { } p1) continue;
                double d = DistanceToSegment(mouse, p0, p1);
                if (d < bestDist) { bestDist = d; best = a; }
            }
        }
        return best;
    }

    /// <summary>Screen-space angle (degrees) of the mouse around the gizmo centre, signed for a rotation about the axis.</summary>
    public double ScreenAngle(int axis, Point mouse, CameraRig camera, Size viewport)
    {
        if (camera.Project(Origin, viewport) is not { } c) return 0;
        double ang = Math.Atan2(-(mouse.Y - c.Y), mouse.X - c.X) * 180 / Math.PI;
        // Counter-clockwise on screen is a positive rotation when the axis points at the camera.
        return Vector3D.DotProduct(Axes[axis], camera.Forward) < 0 ? ang : -ang;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a; var ap = p - a;
        double len2 = ab.LengthSquared;
        double t = len2 < 1e-9 ? 0 : Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}
