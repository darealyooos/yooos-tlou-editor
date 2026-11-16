using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor.Rendering;

/// <summary>Off-screen 3/4 view of a frozen model (UI thread). Used by the asset browser and the catalogue export.</summary>
public static class ThumbnailRenderer
{
    public static Model3DGroup Lights()
    {
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(128, 130, 124)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(214, 200, 176), new Vector3D(-0.5, -0.9, -0.35)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(62, 72, 70), new Vector3D(0.6, 0.2, 0.7)));
        lights.Freeze();
        return lights;
    }

    /// <summary>
    /// <paramref name="frames"/> views evenly spaced around the vertical axis, as one horizontal strip
    /// (frame i at x = i * size). Same framing for every frame, so the object does not change scale while turning.
    /// </summary>
    public static BitmapSource RenderTurntable(Model3DGroup model, N.Vector3 min, N.Vector3 max, int size, int frames, Brush background)
    {
        var center = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        double dist = Math.Max(0.4, (max - min).Length() * 1.55);
        var cam = new PerspectiveCamera { FieldOfView = 35, NearPlaneDistance = 0.02, FarPlaneDistance = 5000, UpDirection = new Vector3D(0, 1, 0) };
        var vp = new Viewport3D { Camera = cam, Width = size, Height = size };
        vp.Children.Add(new ModelVisual3D { Content = Lights() });
        vp.Children.Add(new ModelVisual3D { Content = model });
        var root = new Grid { Width = size, Height = size, Background = background };
        root.Children.Add(vp);
        root.Measure(new Size(size, size));
        root.Arrange(new Rect(0, 0, size, size));

        var strip = new RenderTargetBitmap(size * frames, size, 96, 96, PixelFormats.Pbgra32);
        var frame = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        double p = -20 * Math.PI / 180;
        for (int i = 0; i < frames; i++)
        {
            double y = (-135 + 360.0 * i / frames) * Math.PI / 180;
            var forward = new Vector3D(Math.Cos(p) * Math.Cos(y), Math.Sin(p), Math.Cos(p) * Math.Sin(y));
            cam.Position = center - forward * dist;
            cam.LookDirection = forward;
            root.UpdateLayout();
            frame.Clear();
            frame.Render(root);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawImage(frame, new Rect(i * size, 0, size, size));
            strip.Render(dv);
        }
        strip.Freeze();
        return strip;
    }

    public static BitmapSource Render(Model3DGroup model, N.Vector3 min, N.Vector3 max, int size, Brush background)
    {
        var center = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        double dist = Math.Max(0.4, (max - min).Length() * 1.55);
        double y = -135 * Math.PI / 180, p = -24 * Math.PI / 180;
        var forward = new Vector3D(Math.Cos(p) * Math.Cos(y), Math.Sin(p), Math.Cos(p) * Math.Sin(y));
        var cam = new PerspectiveCamera(center - forward * dist, forward, new Vector3D(0, 1, 0), 35) { NearPlaneDistance = 0.02, FarPlaneDistance = 5000 };
        var vp = new Viewport3D { Camera = cam, Width = size, Height = size };
        vp.Children.Add(new ModelVisual3D { Content = Lights() });
        vp.Children.Add(new ModelVisual3D { Content = model });
        var root = new Grid { Width = size, Height = size, Background = background };
        root.Children.Add(vp);
        root.Measure(new Size(size, size));
        root.Arrange(new Rect(0, 0, size, size));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }
}
