using System.Windows;
using System.Windows.Media.Media3D;
using FactionsReborn.WorldEditor.Core.Scene;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor.Rendering;

/// <summary>Free-fly camera (yaw/pitch) on a WPF PerspectiveCamera, plus screen → world rays.</summary>
public sealed class CameraRig
{
    public PerspectiveCamera Camera { get; } = new() { FieldOfView = 70, NearPlaneDistance = 0.1, FarPlaneDistance = 6000, UpDirection = new Vector3D(0, 1, 0) };
    public Point3D Position { get; private set; } = new(10, 8, 10);
    public double Yaw { get; private set; } = -135;   // degrees, 0 = +X, 90 = +Z
    public double Pitch { get; private set; } = -25;

    public CameraRig() => Apply();

    public Vector3D Forward
    {
        get
        {
            double y = Yaw * Math.PI / 180, p = Pitch * Math.PI / 180;
            return new Vector3D(Math.Cos(p) * Math.Cos(y), Math.Sin(p), Math.Cos(p) * Math.Sin(y));
        }
    }
    public Vector3D Right { get { var r = Vector3D.CrossProduct(Forward, new Vector3D(0, 1, 0)); r.Normalize(); return r; } }
    public Vector3D Up => Vector3D.CrossProduct(Right, Forward);

    public void Look(double dYaw, double dPitch)
    {
        Yaw += dYaw;
        Pitch = Math.Clamp(Pitch + dPitch, -89, 89);
        Apply();
    }

    public void Move(Vector3D delta) { Position += delta; Apply(); }

    public void Set(Point3D position, double yaw, double pitch) { Position = position; Yaw = yaw; Pitch = Math.Clamp(pitch, -89, 89); Apply(); }

    /// <summary>Places the camera to see a box from a 3/4 view.</summary>
    public void Frame(Point3D min, Point3D max)
    {
        var center = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        // Never frame beyond what the far plane can show (bad bounds once sent the camera to ~1e43 m).
        double radius = Math.Clamp((max - min).Length / 2, 1, 2500);
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(center.Z)) center = new Point3D(0, 0, 0);
        Yaw = -135; Pitch = -30;
        Position = center - Forward * radius * 1.6;
        Apply();
    }

    public void FocusOn(Point3D target, double radius)
    {
        Position = target - Forward * Math.Max(2, radius * 2.5);
        Apply();
    }

    private void Apply()
    {
        Camera.Position = Position;
        Camera.LookDirection = Forward;
        Camera.UpDirection = new Vector3D(0, 1, 0);
    }

    /// <summary>World ray through a viewport pixel. WPF's FieldOfView is horizontal.</summary>
    public Ray RayAt(Point pixel, Size viewport)
    {
        double w = Math.Max(1, viewport.Width), h = Math.Max(1, viewport.Height);
        double x = 2 * pixel.X / w - 1, y = 1 - 2 * pixel.Y / h;
        double tanH = Math.Tan(Camera.FieldOfView * Math.PI / 360);
        var dir = Forward + Right * (x * tanH) + Up * (y * tanH * h / w);
        dir.Normalize();
        return new Ray(new N.Vector3((float)Position.X, (float)Position.Y, (float)Position.Z), new N.Vector3((float)dir.X, (float)dir.Y, (float)dir.Z));
    }

    /// <summary>World point → viewport pixel (null when behind the camera).</summary>
    public Point? Project(Point3D p, Size viewport)
    {
        var d = p - Position;
        double z = Vector3D.DotProduct(d, Forward);
        if (z <= 0.01) return null;
        double tanH = Math.Tan(Camera.FieldOfView * Math.PI / 360);
        double x = Vector3D.DotProduct(d, Right) / (z * tanH);
        double y = Vector3D.DotProduct(d, Up) / (z * tanH * viewport.Height / viewport.Width);
        return new Point((x + 1) / 2 * viewport.Width, (1 - y) / 2 * viewport.Height);
    }
}
