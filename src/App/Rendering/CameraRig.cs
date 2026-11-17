using System.Windows;
using System.Windows.Media.Media3D;
using FactionsReborn.WorldEditor.Core.Scene;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor.Rendering;

/// <summary>
/// Free-fly camera (yaw/pitch) over a WPF perspective or orthographic camera, plus screen → world rays and
/// world → screen projection for both projections.
/// </summary>
public sealed class CameraRig
{
    /// <summary>Orthographic rays start this far behind the camera so geometry behind the eye stays visible and pickable.</summary>
    private const double OrthoDepth = 3000;

    public PerspectiveCamera Perspective { get; } = new() { FieldOfView = 70, NearPlaneDistance = 0.1, FarPlaneDistance = 6000, UpDirection = new Vector3D(0, 1, 0) };
    public OrthographicCamera Ortho { get; } = new() { Width = 40, NearPlaneDistance = -OrthoDepth, FarPlaneDistance = 6000, UpDirection = new Vector3D(0, 1, 0) };

    /// <summary>Distance from a ray's origin to the eye: rays from <see cref="RayAt"/> start this far behind the camera.</summary>
    public double RayStart => Orthographic ? OrthoDepth : 0;

    /// <summary>The camera the viewport renders with (changes when <see cref="Orthographic"/> changes).</summary>
    public ProjectionCamera Camera => Orthographic ? Ortho : Perspective;
    public bool Orthographic { get; private set; }
    /// <summary>Raised when <see cref="Camera"/> is replaced (projection switch).</summary>
    public event Action? CameraChanged;

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

    /// <summary>Visible width in metres at <paramref name="distance"/> (constant in orthographic).</summary>
    public double ViewWidthAt(double distance) =>
        Orthographic ? Ortho.Width : 2 * Math.Max(0.01, distance) * Math.Tan(Perspective.FieldOfView * Math.PI / 360);

    /// <summary>
    /// Switches projection. Entering orthographic keeps the apparent size of what is <paramref name="focusDistance"/> metres
    /// ahead; leaving it moves the eye back so the same width is visible in perspective.
    /// </summary>
    public void SetOrthographic(bool on, double focusDistance)
    {
        if (on == Orthographic) return;
        double tanH = Math.Tan(Perspective.FieldOfView * Math.PI / 360);
        if (on)
        {
            Ortho.Width = Math.Clamp(2 * Math.Max(0.5, focusDistance) * tanH, 0.5, 5000);
        }
        else
        {
            // Keep the focus point where it was: pull the eye back to the distance that shows the ortho width.
            var focus = Position + Forward * Math.Max(0.5, focusDistance);
            double d = Ortho.Width / (2 * tanH);
            Position = focus - Forward * d;
        }
        Orthographic = on;
        Apply();
        CameraChanged?.Invoke();
    }

    /// <summary>Orthographic zoom: scales the visible width (factor &lt; 1 zooms in).</summary>
    public void Zoom(double factor)
    {
        Ortho.Width = Math.Clamp(Ortho.Width * factor, 0.5, 5000);
    }

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
        if (Orthographic) Ortho.Width = Math.Clamp(radius * 2.2, 0.5, 5000);
        Apply();
    }

    public void FocusOn(Point3D target, double radius)
    {
        Position = target - Forward * Math.Max(2, radius * 2.5);
        if (Orthographic) Ortho.Width = Math.Clamp(radius * 4, 0.5, 5000);
        Apply();
    }

    private void Apply()
    {
        foreach (var c in new ProjectionCamera[] { Perspective, Ortho })
        {
            c.Position = Position;
            c.LookDirection = Forward;
            c.UpDirection = new Vector3D(0, 1, 0);
        }
    }

    /// <summary>World ray through a viewport pixel. WPF's FieldOfView and orthographic Width are horizontal.</summary>
    public Ray RayAt(Point pixel, Size viewport)
    {
        double w = Math.Max(1, viewport.Width), h = Math.Max(1, viewport.Height);
        double x = 2 * pixel.X / w - 1, y = 1 - 2 * pixel.Y / h;
        if (Orthographic)
        {
            double half = Ortho.Width / 2;
            var o = Position + Right * (x * half) + Up * (y * half * h / w) - Forward * OrthoDepth;
            var f = Forward;
            return new Ray(new N.Vector3((float)o.X, (float)o.Y, (float)o.Z), new N.Vector3((float)f.X, (float)f.Y, (float)f.Z));
        }
        double tanH = Math.Tan(Perspective.FieldOfView * Math.PI / 360);
        var dir = Forward + Right * (x * tanH) + Up * (y * tanH * h / w);
        dir.Normalize();
        return new Ray(new N.Vector3((float)Position.X, (float)Position.Y, (float)Position.Z), new N.Vector3((float)dir.X, (float)dir.Y, (float)dir.Z));
    }

    /// <summary>World point → viewport pixel (null when behind the camera / outside the orthographic depth).</summary>
    public Point? Project(Point3D p, Size viewport)
    {
        var d = p - Position;
        double z = Vector3D.DotProduct(d, Forward);
        double x, y;
        if (Orthographic)
        {
            if (z < -OrthoDepth) return null;
            double half = Ortho.Width / 2;
            x = Vector3D.DotProduct(d, Right) / half;
            y = Vector3D.DotProduct(d, Up) / (half * viewport.Height / viewport.Width);
        }
        else
        {
            if (z <= 0.01) return null;
            double tanH = Math.Tan(Perspective.FieldOfView * Math.PI / 360);
            x = Vector3D.DotProduct(d, Right) / (z * tanH);
            y = Vector3D.DotProduct(d, Up) / (z * tanH * viewport.Height / viewport.Width);
        }
        return new Point((x + 1) / 2 * viewport.Width, (1 - y) / 2 * viewport.Height);
    }
}
