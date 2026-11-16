using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Scene;
using FactionsReborn.WorldEditor.Rendering;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor;

/// <summary>
/// --selftest out.frscene: drives the editor's own code paths (placement raycast, gizmo drag maths fed with
/// projected screen points, duplicate, spawns, boundary, save, reopen) and writes a report next to the output.
/// Mouse/keyboard event delivery itself is not exercised.
/// </summary>
public partial class MainWindow
{
    public string? SelfTestPath { get; set; }

    private async Task<bool> RunSelfTestAsync(string outPath)
    {
        var log = new List<string>();
        bool ok = true;
        void Check(bool cond, string what) { log.Add((cond ? "PASS " : "FAIL ") + what); ok &= cond; }

        Check(_maps.Count == 1, $"map open ({_maps.Count})");
        if (_maps.Count == 0) { File.WriteAllLines(outPath + ".log", log); return false; }
        var map = _maps[0];

        // 1. Place a native prototype (building) at the point under the view centre.
        var proto = map.Pak.Geometry.Prototypes.First(p => p.Name.EndsWith("mil-hesco-2x2m-b", StringComparison.Ordinal));
        var center = ViewCenterPoint();
        Check(center.Y > map.BoundsMin.Y - 1 && center.Y < map.BoundsMax.Y + 1, $"view-centre raycast hits the map at {Fmt(center)}");
        await PlaceAsync(new BrowserItem("hesco", map.Ref, proto.Index, SceneCategories.Buildings, null), center);
        var hesco = _doc.Objects.Last();
        Check(_objectPaks.ContainsKey(hesco.Id), "hesco prototype mesh loaded");

        // 2. Place a real actor prop next to it.
        var bucketEntry = _index.First(e => e.Kind == AssetKind.Actor && e.Name == "bucket-rusty-a");
        await PlaceAsync(new BrowserItem("bucket", bucketEntry.Ref, null, bucketEntry.Category, bucketEntry), center + new N.Vector3(2, 0, 0));
        var bucket = _doc.Objects.Last();
        Check(_objectPaks.ContainsKey(bucket.Id) && ReferenceEquals(_selected, bucket), "bucket placed and selected");

        // 3. Clicking on the bucket selects it (object raycast through the screen projection).
        if (_camera.Project(new Point3D(bucket.Position.X, bucket.Position.Y + 0.15, bucket.Position.Z), ViewSize) is { } bucketPixel)
        {
            Select(null);
            Select(PickSelectable(_camera.RayAt(bucketPixel, ViewSize)));
            Check(ReferenceEquals(_selected, bucket), "screen pick selects the bucket");
        }
        else Check(false, "bucket is in front of the camera");
        Select(bucket);

        // 4. Gizmo move along X by 1.5 m: drag from the projected origin to the projected origin + 1.5 X.
        SetTool(GizmoMode.Move);
        var start = bucket.Position;
        var p0 = _camera.Project(new Point3D(start.X + 0.5, start.Y, start.Z), ViewSize);
        var p1 = _camera.Project(new Point3D(start.X + 2.0, start.Y, start.Z), ViewSize);
        if (p0 is { } a && p1 is { } b)
        {
            Check(_gizmo.Pick(a, _camera, ViewSize) == 0, "gizmo X handle picked under the mouse");
            BeginDrag(0, a); ContinueDrag(b); _dragAxis = -1;
            Check(MathF.Abs(bucket.Position.X - (start.X + 1.5f)) < 0.05f && MathF.Abs(bucket.Position.Z - start.Z) < 1e-4f,
                $"move drag X +1.5 m -> {Fmt(bucket.Position)}");
        }
        else Check(false, "gizmo projected on screen");

        // 5. Gizmo rotate about Y: a quarter turn of the mouse around the projected centre.
        SetTool(GizmoMode.Rotate);
        if (_camera.Project(new Point3D(bucket.Position.X, bucket.Position.Y, bucket.Position.Z), ViewSize) is { } c)
        {
            BeginDrag(1, new Point(c.X + 60, c.Y));
            ContinueDrag(new Point(c.X, c.Y - 60));
            _dragAxis = -1;
            Check(MathF.Abs(MathF.Abs(bucket.RotationDegrees.Y) - 90) < 1, $"rotate drag quarter turn -> yaw {bucket.RotationDegrees.Y:0.#}°");
        }
        SetTool(GizmoMode.Move);

        // 6. Duplicate, spawns, boundary.
        OnDuplicate(null, null);
        await WhenIdleAsync();
        Check(_doc.Objects.Count == 3, $"duplicate -> {_doc.Objects.Count} objects");
        AddSpawn(Team.WLF);
        AddSpawn(Team.Seraphites);
        _doc.Spawns[1].Position += new N.Vector3(0, 0, 12);
        foreach (var off in new[] { new N.Vector2(-20, -20), new N.Vector2(20, -20), new N.Vector2(20, 20), new N.Vector2(-20, 20) })
            _doc.Boundary.Points.Add(new N.Vector2(center.X, center.Z) + off);
        _doc.Boundary.MinY = center.Y - 5; _doc.Boundary.MaxY = center.Y + 20;
        RebuildHelpers(); RebuildHierarchy();
        Check(_doc.Spawns.All(s => _doc.Boundary.Contains(s.Position)), "spawns inside the boundary");

        // 6b. Undo / redo through the recorded history (the duplicate, then a move).
        int objectsBefore = _doc.Objects.Count;
        Undo(); Undo(); Undo(); // two spawns, then the duplicate
        Check(_doc.Objects.Count == objectsBefore - 1 && _doc.Spawns.Count == 0, $"undo x3 -> {_doc.Objects.Count} objects, {_doc.Spawns.Count} spawns");
        Redo(); Redo(); Redo();
        Check(_doc.Objects.Count == objectsBefore && _doc.Spawns.Count == 2, $"redo x3 -> {_doc.Objects.Count} objects, {_doc.Spawns.Count} spawns");
        _doc.Spawns[1].Position = _doc.Spawns[0].Position + new N.Vector3(0, 0, 12);
        var target = _doc.Objects[0];
        Select(target);
        var posBefore = target.Position;
        RecordUndo("move"); target.Position += new N.Vector3(3, 0, 0); UpdateObjectTransform(target);
        Undo();
        var restored = _doc.Objects.First(o => o.Id == target.Id);
        Check(restored.Position == posBefore && _objectVisuals.ContainsKey(restored.Id), "undo move restores position and keeps the mesh");
        Redo(); Undo();

        // 6c. Central shortcut routing + rebinding (in memory; automated runs never write preferences).
        Check(_bindings.Resolve(new Core.Editor.KeyChord("W", Core.Editor.Mods.None))?.Id == "tool.move", "W resolves to Move tool");
        Check(_bindings.Resolve(new Core.Editor.KeyChord("Z", Core.Editor.Mods.Ctrl))?.Id == "edit.undo", "Ctrl+Z resolves to Undo");
        _bindings.Set("tool.move", new Core.Editor.KeyChord("M", Core.Editor.Mods.None));
        Check(_bindings.Resolve(new Core.Editor.KeyChord("M", Core.Editor.Mods.None))?.Id == "tool.move" && _bindings.Resolve(new Core.Editor.KeyChord("W", Core.Editor.Mods.None)) is null, "rebinding Move to M frees W");
        Check(KeyMove.Text == "M" && MenuMove.InputGestureText == "M", "tool button and menu labels follow the binding");
        _bindings.Reset("tool.move");
        _actions["tool.select"]();
        Check(_tool is null && !_gizmo.Visible, "Select tool hides the gizmo");
        _actions["tool.move"]();
        Check(_tool == GizmoMode.Move && _gizmo.Visible, "Move tool shows the gizmo on the selection");

        // 6d. Panel toggles.
        _actions["view.browser"]();
        Check(BrowserPanel.Visibility == Visibility.Collapsed && LeftColumn.Width.Value == 0, "Shift+A action hides the Asset Browser");
        _actions["view.browser"]();
        Check(BrowserPanel.Visibility == Visibility.Visible, "Asset Browser restored");
        _actions["view.inspector"](); _actions["view.hierarchy"]();
        Check(ScenePanel.Visibility == Visibility.Collapsed, "hiding Inspector and Hierarchy collapses the right panel");
        _actions["view.inspector"](); _actions["view.hierarchy"]();
        Check(ScenePanel.Visibility == Visibility.Visible && InspectorPanel.Visibility == Visibility.Visible, "right panel restored");
        UpdateLayout();

        // 7. Save, reopen, compare.
        var before = System.Text.Json.JsonSerializer.Serialize(_doc.Objects, SceneDocument.Json);
        Check(SaveScene(outPath), $"saved {outPath}");
        await OpenSceneAsync(outPath);
        await WhenIdleAsync();
        var after = System.Text.Json.JsonSerializer.Serialize(_doc.Objects, SceneDocument.Json);
        Check(before == after, "objects identical after reopen");
        Check(_doc.Spawns.Count == 2 && _doc.Boundary.Points.Count == 4 && _doc.Maps.Count == 1, "spawns, boundary and map restored");
        Check(_doc.Objects.All(o => _objectPaks.ContainsKey(o.Id)), "all reopened objects have their real meshes");
        Check(_maps.Count == 1 && _objectVisuals.Count == 3, "map and object visuals rebuilt");

        log.Add(ok ? "SELFTEST OK" : "SELFTEST FAILED");
        File.WriteAllLines(outPath + ".log", log);
        return ok;
    }
}
