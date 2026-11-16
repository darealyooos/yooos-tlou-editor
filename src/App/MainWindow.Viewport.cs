using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using FactionsReborn.WorldEditor.Core.Pak;
using FactionsReborn.WorldEditor.Core.Scene;
using FactionsReborn.WorldEditor.Rendering;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor;

public partial class MainWindow
{
    private static readonly Color WlfColor = Color.FromRgb(92, 141, 184);
    private static readonly Color ScarColor = Color.FromRgb(181, 82, 59);
    private static readonly Color BoundaryColor = Color.FromRgb(226, 160, 72);
    private static readonly Color SelectionColor = Color.FromRgb(232, 184, 110);

    private Point _lastMouse;
    private bool _looking, _panning, _orbiting;
    private Point3D _orbitPivot;
    private int _dragAxis = -1;
    private Point _dragStartMouse;
    private double _dragStartParam, _dragStartAngle;
    private N.Vector3 _dragStartPos, _dragStartRot, _dragStartScale;
    private float _dragStartYaw;
    private DateTime _lastFrame = DateTime.UtcNow;
    private int _frames;
    private TimeSpan _lastRenderTime;
    private DateTime _fpsWindow = DateTime.UtcNow;
    private double _zoomVelocity; // metres per second, decays (smooth zoom)
    /// <summary>null = Select tool (no gizmo).</summary>
    private GizmoMode? _tool = GizmoMode.Move;

    private void SetupViewport()
    {
        Viewport.Camera = _camera.Camera;
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(120, 120, 125)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(200, 195, 185), new Vector3D(-0.4, -1, -0.3)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(70, 75, 90), new Vector3D(0.5, 0.3, 0.6)));
        lights.Freeze();
        Viewport.Children.Add(new ModelVisual3D { Content = lights });
        Viewport.Children.Add(_gridVisual);
        Viewport.Children.Add(_mapRoot);
        Viewport.Children.Add(_objectRoot);
        Viewport.Children.Add(_helperRoot);
        Viewport.Children.Add(_selectionVisual);
        Viewport.Children.Add(_gizmo.Visual);
        _gridVisual.Content = Shapes.Model(Shapes.Grid(50, 1, 0, 0.02), Shapes.Emissive(Color.FromRgb(44, 50, 42)));
        CompositionTarget.Rendering += OnFrame;
    }

    private Size ViewSize => new(ViewportHost.ActualWidth, ViewportHost.ActualHeight);

    // ---------------------------------------------------------------- per-frame fly, smooth zoom, fps

    private void OnFrame(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per composed frame; count distinct frames only.
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == _lastRenderTime) return;
            _lastRenderTime = r.RenderingTime;
        }
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        var vp = _settings.Viewport;
        if (_looking)
        {
            bool fast = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            double speed = vp.FlySpeed * (fast ? vp.FastMultiplier : 1) * dt;
            var move = new Vector3D();
            if (FlyKeyDown("fly.forward")) move += _camera.Forward;
            if (FlyKeyDown("fly.back")) move -= _camera.Forward;
            if (FlyKeyDown("fly.right")) move += _camera.Right;
            if (FlyKeyDown("fly.left")) move -= _camera.Right;
            if (FlyKeyDown("fly.up")) move += new Vector3D(0, 1, 0);
            if (FlyKeyDown("fly.down")) move -= new Vector3D(0, 1, 0);
            if (move.Length > 0) { move.Normalize(); _camera.Move(move * speed); RefreshGizmo(); }
        }
        if (Math.Abs(_zoomVelocity) > 0.01)
        {
            _camera.Move(_camera.Forward * _zoomVelocity * dt);
            _zoomVelocity *= Math.Exp(-10 * dt);
            RefreshGizmo();
        }
        _frames++;
        if ((now - _fpsWindow).TotalSeconds >= 1)
        {
            int tris = _maps.Sum(m => m.Triangles);
            StatusFps.Text = $"{_frames / (now - _fpsWindow).TotalSeconds:0}";
            StatusTris.Text = _maps.Count == 0 ? "—" : $"{tris:N0} tris";
            StatusCamera.Text = $"{_camera.Position.X:0.0}, {_camera.Position.Y:0.0}, {_camera.Position.Z:0.0}  ·  yaw {_camera.Yaw % 360:0}° pitch {_camera.Pitch:0}°";
            _frames = 0; _fpsWindow = now;
        }
    }

    // ---------------------------------------------------------------- mouse

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewportHost.Focus();
        var pos = e.GetPosition(ViewportHost);
        _lastMouse = pos;
        if (e.ChangedButton == MouseButton.Right) { _looking = true; ViewportHost.CaptureMouse(); return; }
        if (e.ChangedButton == MouseButton.Middle) { _panning = true; ViewportHost.CaptureMouse(); return; }
        if (e.ChangedButton != MouseButton.Left) return;

        if (_settings.Viewport.AltOrbit && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Orbit around the selection, or the point under the cursor.
            var p = SelectedPosition() ?? PickPoint(pos);
            _orbitPivot = new Point3D(p.X, p.Y, p.Z);
            _orbiting = true;
            ViewportHost.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (ToolBoundary.IsChecked == true)
        {
            var p = PickPoint(pos);
            RecordUndo("boundary point");
            if (_doc.Boundary.Points.Count == 0) { _doc.Boundary.MinY = p.Y - 5; _doc.Boundary.MaxY = p.Y + 30; }
            _doc.Boundary.Points.Add(new N.Vector2(p.X, p.Z));
            MarkDirty(); RebuildHelpers(); RebuildHierarchy();
            Status($"Boundary point {_doc.Boundary.Points.Count} at ({p.X:0.#}, {p.Z:0.#}). Esc or the tool button to finish.");
            UpdateBoundaryBanner();
            return;
        }

        int axis = _tool is null ? -1 : _gizmo.Pick(pos, _camera, ViewSize);
        if (axis >= 0 && _selected is not null)
        {
            RecordUndo(_gizmo.Mode switch { GizmoMode.Rotate => "rotate", GizmoMode.Scale => "scale", _ => "move" });
            BeginDrag(axis, pos);
            ViewportHost.CaptureMouse();
            return;
        }
        Select(PickSelectable(_camera.RayAt(pos, ViewSize)));
        RebuildHierarchy();
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) _looking = false;
        if (e.ChangedButton == MouseButton.Middle) _panning = false;
        if (e.ChangedButton == MouseButton.Left) _orbiting = false;
        if (e.ChangedButton == MouseButton.Left && _dragAxis >= 0) { _dragAxis = -1; ShowProperties(); }
        if (!_looking && !_panning && !_orbiting && _dragAxis < 0) ViewportHost.ReleaseMouseCapture();
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(ViewportHost);
        var d = pos - _lastMouse;
        _lastMouse = pos;
        var vp = _settings.Viewport;
        double k = 0.25 * vp.LookSensitivity;
        double dy = vp.InvertY ? d.Y : -d.Y;
        if (_looking) { _camera.Look(d.X * k, dy * k); RefreshGizmo(); return; }
        if (_orbiting)
        {
            double dist = Math.Max(0.5, (_orbitPivot - _camera.Position).Length);
            _camera.Look(d.X * k, dy * k);
            _camera.Set(_orbitPivot - _camera.Forward * dist, _camera.Yaw, _camera.Pitch);
            RefreshGizmo();
            return;
        }
        if (_panning)
        {
            double pk = 0.002 * Math.Max(5, (_gizmo.Visible ? (_gizmo.Origin - _camera.Position).Length : 20));
            _camera.Move(-_camera.Right * d.X * pk + _camera.Up * d.Y * pk);
            RefreshGizmo();
            return;
        }
        if (_dragAxis >= 0) { ContinueDrag(pos); return; }
        if (_tool is null) return;
        int hot = _gizmo.Pick(pos, _camera, ViewSize);
        if (hot != _gizmo.HotAxis) { _gizmo.HotAxis = hot; RefreshGizmo(); }
    }

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        bool fast = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        double step = (fast ? 0.02 : 0.005) * e.Delta * _settings.Viewport.ZoomSpeed;
        if (_settings.Viewport.SmoothZoom) _zoomVelocity += step * 10; // integrated over ~0.1 s in OnFrame
        else { _camera.Move(_camera.Forward * step); RefreshGizmo(); }
    }

    private void ResetCamera()
    {
        _zoomVelocity = 0;
        if (_maps.Count > 0 || _doc.Objects.Count > 0) FrameAll();
        else _camera.Set(new Point3D(10, 8, 10), -135, -25);
        RefreshGizmo();
        Status("Camera reset.");
    }

    private void OnResetCamera(object sender, RoutedEventArgs e) => ResetCamera();

    // ---------------------------------------------------------------- tools

    private void OnToolSelect(object sender, RoutedEventArgs e) => SetTool(null);
    private void OnToolMove(object sender, RoutedEventArgs e) => SetTool(GizmoMode.Move);
    private void OnToolRotate(object sender, RoutedEventArgs e) => SetTool(GizmoMode.Rotate);
    private void OnToolScale(object sender, RoutedEventArgs e) => SetTool(GizmoMode.Scale);
    private void OnUndo(object sender, RoutedEventArgs e) => Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void SetTool(GizmoMode? mode)
    {
        _tool = mode;
        if (mode is { } m) _gizmo.Mode = m;
        ToolSelect.IsChecked = mode is null;
        ToolMove.IsChecked = mode == GizmoMode.Move;
        ToolRotate.IsChecked = mode == GizmoMode.Rotate;
        ToolScale.IsChecked = mode == GizmoMode.Scale;
        RefreshGizmo();
    }

    private void OnToolBoundary(object sender, RoutedEventArgs e) => SetBoundaryTool(ToolBoundary.IsChecked == true);

    private void SetBoundaryTool(bool on)
    {
        ToolBoundary.IsChecked = on;
        if (on)
        {
            if (_workspace != Workspace.World) SetWorkspace(Workspace.World);
            Select(null);
            Status("Click on the ground to add boundary points (XZ polygon).");
            ViewportHost.Focus();
        }
        else Status("Boundary finished.");
        UpdateBoundaryBanner();
    }

    private void UpdateBoundaryBanner()
    {
        bool on = ToolBoundary.IsChecked == true;
        BoundaryBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) BoundaryBannerText.Text = $"Drawing boundary · {_doc.Boundary.Points.Count} point(s) · click the ground to add · Esc to finish";
    }

    private void OnFrameAll(object? sender, RoutedEventArgs? e)
    {
        FrameAll();
        RefreshGizmo();
    }

    private void OnFocus(object? sender, RoutedEventArgs? e)
    {
        if (SelectedPosition() is { } p) _camera.FocusOn(new Point3D(p.X, p.Y, p.Z), 3);
        else FrameAll();
        RefreshGizmo();
    }

    private void FrameAll()
    {
        if (_maps.Count > 0)
        {
            var min = _maps.Select(m => m.BoundsMin).Aggregate(N.Vector3.Min);
            var max = _maps.Select(m => m.BoundsMax).Aggregate(N.Vector3.Max);
            _camera.Frame(new Point3D(min.X, min.Y, min.Z), new Point3D(max.X, max.Y, max.Z));
        }
        else if (_doc.Objects.Count > 0)
        {
            var min = _doc.Objects.Select(o => o.Position).Aggregate(N.Vector3.Min) - new N.Vector3(3);
            var max = _doc.Objects.Select(o => o.Position).Aggregate(N.Vector3.Max) + new N.Vector3(3);
            _camera.Frame(new Point3D(min.X, min.Y, min.Z), new Point3D(max.X, max.Y, max.Z));
        }
    }

    private void OnToggleLayers(object sender, RoutedEventArgs e) => ApplyLayerVisibility();

    private void ApplyLayerVisibility()
    {
        if (_settings is not null && _settings.Viewport.ShowGrid != ShowGrid.IsChecked) { _settings.Viewport.ShowGrid = ShowGrid.IsChecked; SaveSettingsSoon(); }
        ChipMap.IsChecked = ShowMap.IsChecked;
        ChipVegetation.IsChecked = ShowVegetation.IsChecked;
        ChipGrid.IsChecked = ShowGrid.IsChecked;
        foreach (var m in _maps)
        {
            bool show = ShowMap.IsChecked;
            SetChild(_mapRoot, m.Solid, show);
            SetChild(_mapRoot, m.Vegetation, show && ShowVegetation.IsChecked);
        }
        SetChild(Viewport, _gridVisual, ShowGrid.IsChecked);
    }

    private static void SetChild(ModelVisual3D parent, Visual3D child, bool present)
    {
        if (present && !parent.Children.Contains(child)) parent.Children.Add(child);
        else if (!present) parent.Children.Remove(child);
    }

    private static void SetChild(System.Windows.Controls.Viewport3D parent, Visual3D child, bool present)
    {
        if (present && !parent.Children.Contains(child)) parent.Children.Insert(1, child);
        else if (!present) parent.Children.Remove(child);
    }

    // ---------------------------------------------------------------- picking

    /// <summary>World point under a viewport pixel: map triangles, then placed objects, then the ground plane.</summary>
    private N.Vector3 PickPoint(Point pixel)
    {
        var ray = _camera.RayAt(pixel, ViewSize);
        float? best = null;
        foreach (var m in _maps)
            if (Raycast.Instances(ray, m.Pak.Geometry, m.Instances, best ?? float.MaxValue) is { } t) best = t;
        foreach (var o in _doc.Objects)
            if (ObjectHit(ray, o, best ?? float.MaxValue) is { } t) best = t;
        best ??= Raycast.Plane(ray, _maps.Count > 0 ? _maps.Min(m => m.BoundsMin.Y) : 0);
        return ray.At(best is { } b && b < 2000 ? b : 15);
    }

    private N.Vector3 ViewCenterPoint() => PickPoint(new Point(ViewSize.Width / 2, ViewSize.Height / 2));

    private float? ObjectHit(Ray ray, SceneObject o, float max)
    {
        if (!_objectPaks.TryGetValue(o.Id, out var pak)) return null;
        var geo = pak.Geometry;
        if (geo.Prototypes.Count == 0) return null;
        return Raycast.Instances(ray, geo, [(o.Prototype ?? 0, o.EngineMatrix())], max);
    }

    private object? PickSelectable(Ray ray)
    {
        object? hit = null;
        float best = float.MaxValue;
        foreach (var o in _doc.Objects)
            if (ObjectHit(ray, o, best) is { } t) { best = t; hit = o; }
        foreach (var s in _doc.Spawns)
            if (Raycast.Aabb(ray, s.Position - new N.Vector3(0.4f, 0, 0.4f), s.Position + new N.Vector3(0.4f, 1.9f, 0.4f)) is { } t && t < best) { best = t; hit = s; }
        return hit;
    }

    // ---------------------------------------------------------------- selection + gizmo

    private void Select(object? item)
    {
        _selected = item;
        _gizmo.HotAxis = -1;
        if (item is not null) ShortcutSheet.Visibility = Visibility.Collapsed;
        UpdateSelectionVisuals();
        ShowProperties();
    }

    private N.Vector3? SelectedPosition() => _selected switch
    {
        SceneObject o => o.Position,
        SpawnPoint s => s.Position,
        _ => null,
    };

    private void UpdateSelectionVisuals()
    {
        _selectionVisual.Content = null;
        _selectionVisual.Transform = Transform3D.Identity;
        if (_selected is SceneObject o)
        {
            var (min, max) = LocalBounds(o);
            _selectionVisual.Content = Shapes.Model(Shapes.WireBox(new Point3D(min.X, min.Y, min.Z), new Point3D(max.X, max.Y, max.Z), Math.Max(0.01, (max - min).Length() * 0.006)), Shapes.Emissive(SelectionColor));
            _selectionVisual.Transform = new MatrixTransform3D(ToWpf(o.EngineMatrix()));
        }
        RefreshGizmo();
    }

    private (N.Vector3 Min, N.Vector3 Max) LocalBounds(SceneObject o)
    {
        if (!_objectPaks.TryGetValue(o.Id, out var pak)) return (new N.Vector3(-0.5f, 0, -0.5f), new N.Vector3(0.5f, 1, 0.5f));
        return ProtoBounds(pak.Geometry, o.Prototype);
    }

    private void RefreshGizmo()
    {
        if (_tool is null || SelectedPosition() is not { } p) { _gizmo.Hide(); return; }
        bool spawn = _selected is SpawnPoint;
        _gizmo.Enabled[0] = _gizmo.Enabled[2] = !(spawn && _gizmo.Mode != GizmoMode.Move);
        _gizmo.Enabled[1] = !(spawn && _gizmo.Mode == GizmoMode.Scale);
        _gizmo.Show(new Point3D(p.X, p.Y, p.Z), _camera);
    }

    private void BeginDrag(int axis, Point mouse)
    {
        _dragAxis = axis;
        _dragStartMouse = mouse;
        _dragStartAngle = _gizmo.ScreenAngle(axis, mouse, _camera, ViewSize);
        switch (_selected)
        {
            case SceneObject o: _dragStartPos = o.Position; _dragStartRot = o.RotationDegrees; _dragStartScale = o.Scale; break;
            case SpawnPoint s: _dragStartPos = s.Position; _dragStartYaw = s.YawDegrees; break;
        }
        _dragStartParam = AxisParameterFrom(new Point3D(_dragStartPos.X, _dragStartPos.Y, _dragStartPos.Z), mouse);
    }

    private void ContinueDrag(Point mouse)
    {
        bool snap = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var axis = Gizmo.Axes[_dragAxis];
        var axisN = new N.Vector3((float)axis.X, (float)axis.Y, (float)axis.Z);
        switch (_gizmo.Mode)
        {
            case GizmoMode.Move:
            {
                // Measured along the axis through the position at drag start (the gizmo moves with the object).
                var startOrigin = new Point3D(_dragStartPos.X, _dragStartPos.Y, _dragStartPos.Z);
                double delta = AxisParameterFrom(startOrigin, mouse) - _dragStartParam;
                if (snap) delta = Math.Round(delta / 0.25) * 0.25;
                var pos = _dragStartPos + axisN * (float)delta;
                if (_selected is SceneObject o) { o.Position = pos; UpdateObjectTransform(o); UpdateSelectionVisuals(); }
                else if (_selected is SpawnPoint s) { s.Position = pos; RebuildHelpers(); RefreshGizmo(); }
                break;
            }
            case GizmoMode.Rotate:
            {
                double delta = _gizmo.ScreenAngle(_dragAxis, mouse, _camera, ViewSize) - _dragStartAngle;
                delta = (delta + 540) % 360 - 180;
                if (snap) delta = Math.Round(delta / 15) * 15;
                if (_selected is SceneObject o)
                {
                    o.RotationDegrees = _dragStartRot + axisN * (float)delta;
                    UpdateObjectTransform(o); UpdateSelectionVisuals();
                }
                else if (_selected is SpawnPoint s && _dragAxis == 1)
                {
                    s.YawDegrees = (float)(((_dragStartYaw + delta) % 360 + 360) % 360);
                    RebuildHelpers();
                }
                break;
            }
            case GizmoMode.Scale:
            {
                if (_selected is not SceneObject o) break;
                double dy = -(mouse.Y - _dragStartMouse.Y) + (mouse.X - _dragStartMouse.X);
                double factor = Math.Exp(dy / 150);
                if (snap) factor = Math.Max(0.1, Math.Round(factor * 10) / 10);
                var s = _dragStartScale;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) s *= (float)factor;
                else s = s + axisN * (float)(Get(s, _dragAxis) * (factor - 1));
                o.Scale = N.Vector3.Max(s, new N.Vector3(0.01f));
                UpdateObjectTransform(o); UpdateSelectionVisuals();
                break;
            }
        }
        MarkDirty();
    }

    /// <summary>Closest-point parameter along the drag axis through a fixed origin.</summary>
    private double AxisParameterFrom(Point3D origin, Point mouse)
    {
        var ray = _camera.RayAt(mouse, ViewSize);
        var a = Gizmo.Axes[_dragAxis];
        var d = new Vector3D(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
        var w0 = origin - new Point3D(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        double b = Vector3D.DotProduct(a, d);
        double denom = 1 - b * b;
        return denom < 1e-6 ? 0 : (b * Vector3D.DotProduct(d, w0) - Vector3D.DotProduct(a, w0)) / denom;
    }

    private static float Get(N.Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    // ---------------------------------------------------------------- spawns + boundary visuals

    private void RebuildHelpers()
    {
        var group = new Model3DGroup();
        foreach (var s in _doc.Spawns)
        {
            var color = s.Team == Team.WLF ? WlfColor : ScarColor;
            var mesh = new MeshGeometry3D();
            Shapes.Box(new Point3D(-0.3, 0, -0.3), new Point3D(0.3, 1.8, 0.3), mesh);
            Shapes.Segment(mesh, new Point3D(0, 1.0, 0), new Point3D(1.2, 1.0, 0), 0.12); // facing arrow along local +X
            Shapes.Box(new Point3D(1.15, 0.9, -0.12), new Point3D(1.4, 1.1, 0.12), mesh);
            var model = Shapes.Model(mesh, Shapes.Emissive(color));
            var t = new Transform3DGroup();
            t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), -s.YawDegrees)));
            t.Children.Add(new TranslateTransform3D(s.Position.X, s.Position.Y, s.Position.Z));
            model.Transform = t;
            group.Children.Add(model);
            if (ReferenceEquals(s, _selected))
            {
                var sel = Shapes.Model(Shapes.WireBox(new Point3D(-0.45, -0.05, -0.45), new Point3D(0.45, 1.95, 0.45), 0.03), Shapes.Emissive(SelectionColor));
                sel.Transform = new TranslateTransform3D(s.Position.X, s.Position.Y, s.Position.Z);
                group.Children.Add(sel);
            }
        }

        var b = _doc.Boundary;
        if (b.Points.Count > 0)
        {
            var posts = new MeshGeometry3D();
            var walls = new MeshGeometry3D();
            for (int i = 0; i < b.Points.Count; i++)
            {
                var p = b.Points[i];
                Shapes.Segment(posts, new Point3D(p.X, b.MinY, p.Y), new Point3D(p.X, b.MaxY, p.Y), 0.25);
                if (b.Points.Count < 2 || (i == b.Points.Count - 1 && b.Points.Count < 3)) continue;
                var q = b.Points[(i + 1) % b.Points.Count];
                int k = walls.Positions.Count;
                walls.Positions.Add(new Point3D(p.X, b.MinY, p.Y)); walls.Positions.Add(new Point3D(q.X, b.MinY, q.Y));
                walls.Positions.Add(new Point3D(q.X, b.MaxY, q.Y)); walls.Positions.Add(new Point3D(p.X, b.MaxY, p.Y));
                foreach (var idx in new[] { 0, 1, 2, 0, 2, 3 }) walls.TriangleIndices.Add(k + idx);
                Shapes.Segment(posts, new Point3D(p.X, b.MinY, p.Y), new Point3D(q.X, b.MinY, q.Y), 0.15);
                Shapes.Segment(posts, new Point3D(p.X, b.MaxY, p.Y), new Point3D(q.X, b.MaxY, q.Y), 0.15);
            }
            group.Children.Add(Shapes.Model(posts, Shapes.Emissive(BoundaryColor)));
            if (walls.TriangleIndices.Count > 0)
                group.Children.Add(Shapes.Model(walls, Shapes.Additive(Color.FromRgb(38, 24, 4))));
        }
        _helperRoot.Content = group;
    }
}
