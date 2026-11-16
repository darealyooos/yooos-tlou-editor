using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FactionsReborn.WorldEditor.Core.Scene;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor;

public partial class MainWindow
{
    private bool _syncingTree;
    private bool _scrubbing;
    private sealed record BoundaryNode;
    private static readonly BoundaryNode Boundary = new();

    private Brush Res(string key) => (Brush)FindResource(key);

    // ---------------------------------------------------------------- outliner

    private void RebuildHierarchy()
    {
        _syncingTree = true;
        try
        {
            Hierarchy.Items.Clear();
            var maps = Group("Maps", _maps.Count);
            foreach (var m in _maps)
                maps.Items.Add(Leaf(Path.GetFileNameWithoutExtension(m.Ref.Path), $"{m.Instances.Count:N0} · locked", m, Res("Text3")));
            Hierarchy.Items.Add(maps);

            foreach (var cat in SceneCategories.Placeable)
            {
                var objs = _doc.Objects.Where(o => o.Category == cat).ToList();
                var node = Group(cat, objs.Count);
                foreach (var o in objs.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)) node.Items.Add(Leaf(o.Name, null, o, Res("Olive")));
                Hierarchy.Items.Add(node);
            }
            var spawns = Group("Spawns", _doc.Spawns.Count);
            foreach (Team team in Enum.GetValues<Team>())
            {
                var list = _doc.Spawns.Where(s => s.Team == team).ToList();
                var swatch = new SolidColorBrush(team == Team.WLF ? Color.FromRgb(92, 141, 184) : Color.FromRgb(181, 82, 59));
                var t = Group(team.ToString(), list.Count, sub: true);
                foreach (var s in list) t.Items.Add(Leaf(s.Name, null, s, swatch));
                spawns.Items.Add(t);
            }
            Hierarchy.Items.Add(spawns);
            Hierarchy.Items.Add(Leaf("Arena boundary", $"{_doc.Boundary.Points.Count} pts", Boundary, Res("Amber")));
            OutlinerCount.Text = $"{_doc.Objects.Count} objects · {_doc.Spawns.Count} spawns";
        }
        finally { _syncingTree = false; }
        UpdateEmptyState();
        if (_selected is null && PropertiesPanel.Children.Count > 0 && (PropertiesPanel.Children[0] as TextBlock)?.Text == NothingSelected) ShowProperties();
        UpdateSelectionStatus();
    }

    private const string NothingSelected = "Nothing selected";

    private TreeViewItem Group(string title, int count, bool sub = false)
    {
        var header = new DockPanel { LastChildFill = true };
        var n = new TextBlock { Text = count.ToString(CultureInfo.InvariantCulture), FontFamily = (FontFamily)FindResource("Mono"), FontSize = 10.5, Foreground = Res("Text4") };
        DockPanel.SetDock(n, Dock.Right);
        header.Children.Add(n);
        header.Children.Add(new TextBlock
        {
            Text = sub ? title : title.ToUpperInvariant(), FontFamily = (FontFamily)FindResource("Display"), FontSize = sub ? 11.5 : 10.5,
            Foreground = Res(sub ? "Text2" : "Text3"), VerticalAlignment = VerticalAlignment.Center,
        });
        return new TreeViewItem { Header = header, IsExpanded = true, Focusable = false };
    }

    private TreeViewItem Leaf(string title, string? meta, object tag, Brush swatch)
    {
        var header = new DockPanel { LastChildFill = true };
        if (meta is not null)
        {
            var m = new TextBlock { Text = meta, FontSize = 10.5, Foreground = Res("Text4"), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(m, Dock.Right);
            header.Children.Add(m);
        }
        var dot = new System.Windows.Shapes.Rectangle { Width = 5, Height = 5, Fill = swatch, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(dot);
        header.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var item = new TreeViewItem { Header = header, Tag = tag, IsExpanded = true };
        if (ReferenceEquals(tag, _selected) || (tag is BoundaryNode && _selected is BoundaryNode)) item.IsSelected = true;
        return item;
    }

    private void OnHierarchySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTree || e.NewValue is not TreeViewItem { Tag: { } tag }) return;
        if (tag is LoadedMap map) { Select(null); ShowMapProperties(map); return; }
        Select(tag);
    }

    private void UpdateSelectionStatus()
    {
        if (StatusSelection is null) return;
        StatusSelection.Text = _selected switch
        {
            SceneObject o => o.Name,
            SpawnPoint s => s.Name,
            BoundaryNode => "Arena boundary",
            _ => "—",
        };
    }

    // ---------------------------------------------------------------- inspector

    private void ShowProperties()
    {
        PropertiesPanel.Children.Clear();
        switch (_selected)
        {
            case SceneObject o: ShowObjectProperties(o); break;
            case SpawnPoint s: ShowSpawnProperties(s); break;
            case BoundaryNode: ShowBoundaryProperties(); break;
            default:
                Heading(NothingSelected, "Click an object in the viewport or the outliner.");
                AddSection(PropertiesPanel, "Scene");
                AddKeyValue(PropertiesPanel, "Maps", $"{_maps.Count}");
                AddKeyValue(PropertiesPanel, "Objects", $"{_doc.Objects.Count}");
                AddKeyValue(PropertiesPanel, "Spawns", $"{_doc.Spawns.Count(s => s.Team == Team.WLF)} WLF · {_doc.Spawns.Count(s => s.Team == Team.Seraphites)} Seraphites");
                AddKeyValue(PropertiesPanel, "Boundary", $"{_doc.Boundary.Points.Count} points");
                break;
        }
        UpdateSelectionStatus();
    }

    private void ShowObjectProperties(SceneObject o)
    {
        Heading(o.Name, o.Prototype is null ? $"Actor · {o.Category}" : $"Map prototype · {o.Category}");
        AddSection(PropertiesPanel, "Object");
        TextRow("Name", o.Name, v => { o.Name = v; RebuildHierarchy(); });
        ComboRow("Category", SceneCategories.Placeable, o.Category, v => { o.Category = v; RebuildHierarchy(); });

        AddSection(PropertiesPanel, "Transform");
        void Refresh() { UpdateObjectTransform(o); UpdateSelectionVisuals(); }
        Vec3Row("Position", "m", o.Position, v => o.Position = v, Refresh, 0.05, null);
        Vec3Row("Rotation", "°", o.RotationDegrees, v => o.RotationDegrees = v, Refresh, 0.5, N.Vector3.Zero);
        Vec3Row("Scale", "×", o.Scale, v => o.Scale = N.Vector3.Max(v, new N.Vector3(0.01f)), Refresh, 0.01, N.Vector3.One);

        AddSection(PropertiesPanel, "Asset");
        AddKeyValue(PropertiesPanel, "Path", o.Asset.Path);
        AddKeyValue(PropertiesPanel, "Archive", o.Asset.Archive);
        if (o.Prototype is int p) AddKeyValue(PropertiesPanel, "Prototype", $"#{p}  {o.PrototypeName}");

        if (_objectPaks.TryGetValue(o.Id, out var pak))
        {
            var geo = pak.Geometry;
            int[] subs = o.Prototype is int pi ? geo.Prototypes[pi].Lod0Submeshes : Core.Pak.GeometryReader.ActorLod0(geo);
            int tris = subs.Sum(s => geo.Submeshes[s].Indices.Length / 3);
            var (min, max) = LocalBounds(o);
            var size = (max - min) * o.Scale;
            AddSection(PropertiesPanel, "Mesh");
            AddKeyValue(PropertiesPanel, "LOD0", $"{subs.Length} submeshes · {tris:N0} tris");
            AddKeyValue(PropertiesPanel, "Size", $"{size.X:0.##} × {size.Y:0.##} × {size.Z:0.##} m");

            var tex = subs.Select(s => geo.Materials[geo.Submeshes[s].MaterialIndex].BaseColorTexture).OfType<string>().Distinct().ToList();
            AddSection(PropertiesPanel, "Materials");
            if (tex.Count == 0) AddNote(PropertiesPanel, "No base-colour texture referenced.");
            foreach (var t in tex) AddKeyValue(PropertiesPanel, pak.Textures.ContainsKey(t) ? "Decoded" : "Missing", Path.GetFileName(Path.GetDirectoryName(t)) ?? t);

            AddSection(PropertiesPanel, "Collision");
            AddNote(PropertiesPanel, pak.HasCollision
                ? "The pak carries Havok (hknp) collision data. Not decoded or editable yet."
                : (o.Prototype is null ? "None in this pak." : "Background collision lives in the level's -phys pak (not decoded yet)."));
        }
        else AddNote(PropertiesPanel, "Mesh still loading, or the asset could not be decoded (shown as a red box).");

        var m = o.EngineMatrix();
        AddSection(PropertiesPanel, "Engine matrix");
        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = $"{m.M11,8:0.###} {m.M12,8:0.###} {m.M13,8:0.###}\n{m.M21,8:0.###} {m.M22,8:0.###} {m.M23,8:0.###}\n{m.M31,8:0.###} {m.M32,8:0.###} {m.M33,8:0.###}\n{m.M41,8:0.###} {m.M42,8:0.###} {m.M43,8:0.###}",
            FontFamily = (FontFamily)FindResource("Mono"), FontSize = 10.5, Foreground = Res("Text3"), Margin = new Thickness(0, 2, 0, 0),
            ToolTip = "Row vectors, translation in the last row (same convention as level instance records).",
        });
        Inside(o.Position);
    }

    private void ShowSpawnProperties(SpawnPoint s)
    {
        Heading(s.Name, $"Spawn point · {s.Team}");
        AddSection(PropertiesPanel, "Spawn");
        TextRow("Name", s.Name, v => { s.Name = v; RebuildHierarchy(); });
        ComboRow("Team", Enum.GetNames<Team>(), s.Team.ToString(), v => { s.Team = Enum.Parse<Team>(v); RebuildHelpers(); RebuildHierarchy(); });
        AddSection(PropertiesPanel, "Transform");
        Vec3Row("Position", "m", s.Position, v => s.Position = v, () => { RebuildHelpers(); RefreshGizmo(); }, 0.05, null);
        ScalarRow("Yaw", "°", s.YawDegrees, v => { s.YawDegrees = v; RebuildHelpers(); }, 0.5, "0 = +X, 90 = +Z");
        Inside(s.Position);
    }

    private void ShowBoundaryProperties()
    {
        var b = _doc.Boundary;
        Heading("Arena boundary", $"{b.Points.Count} points on the XZ plane");
        AddSection(PropertiesPanel, "Height range");
        ScalarRow("Min Y", "m", b.MinY, v => { b.MinY = v; RebuildHelpers(); }, 0.1, null);
        ScalarRow("Max Y", "m", b.MaxY, v => { b.MaxY = v; RebuildHelpers(); }, 0.1, null);
        AddSection(PropertiesPanel, "Points");
        AddNote(PropertiesPanel, "Use the Boundary tool (B) and click on the ground to add points.");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var draw = new Button { Content = ToolBoundary.IsChecked == true ? "Finish drawing" : "Draw points", Margin = new Thickness(0, 0, 6, 0) };
        draw.Click += (_, _) => { SetBoundaryTool(ToolBoundary.IsChecked != true); };
        row.Children.Add(draw);
        if (b.Points.Count > 0)
        {
            var undo = new Button { Content = "Remove last point", Margin = new Thickness(0, 0, 6, 0) };
            undo.Click += (_, _) => { RecordUndo("remove boundary point"); b.Points.RemoveAt(b.Points.Count - 1); MarkDirty(); RebuildHelpers(); RebuildHierarchy(); ShowProperties(); };
            row.Children.Add(undo);
        }
        PropertiesPanel.Children.Add(row);
        int outside = _doc.Spawns.Count(s => !b.Contains(s.Position));
        if (b.Points.Count >= 3)
            AddNote(PropertiesPanel, outside == 0 ? "All spawns are inside the arena." : $"⚠ {outside} spawn(s) are outside the arena.", outside == 0 ? null : Res("Danger"));
    }

    private void ShowMapProperties(LoadedMap m)
    {
        PropertiesPanel.Children.Clear();
        Heading(Path.GetFileNameWithoutExtension(m.Ref.Path), "Map · read-only background");
        AddSection(PropertiesPanel, "Source");
        AddKeyValue(PropertiesPanel, "Path", m.Ref.Path);
        AddKeyValue(PropertiesPanel, "Archive", m.Ref.Archive);
        AddSection(PropertiesPanel, "Content");
        AddKeyValue(PropertiesPanel, "Instances", $"{m.Instances.Count:N0}");
        AddKeyValue(PropertiesPanel, "Prototypes", $"{m.Pak.Geometry.Prototypes.Count:N0}");
        AddKeyValue(PropertiesPanel, "LOD0 triangles", $"{m.Triangles:N0}");
        AddKeyValue(PropertiesPanel, "Textures", $"{m.Pak.Textures.Count:N0} base colour");
        AddKeyValue(PropertiesPanel, "Bounds min", Fmt(m.BoundsMin));
        AddKeyValue(PropertiesPanel, "Bounds max", Fmt(m.BoundsMax));
        AddNote(PropertiesPanel, "The map's own instances are read-only. Place copies of its prototypes from the browser (\"Map pieces\").");
    }

    private void Inside(N.Vector3 p)
    {
        if (_doc.Boundary.Points.Count >= 3 && !_doc.Boundary.Contains(p)) AddNote(PropertiesPanel, "⚠ Outside the arena boundary.", Res("Danger"));
    }

    // ---------------------------------------------------------------- shared widgets (inspector + catalogue)

    private void Heading(string title, string subtitle)
    {
        PropertiesPanel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Title"), FontSize = 18, Margin = new Thickness(0, 6, 0, 0), ToolTip = title });
        PropertiesPanel.Children.Add(new TextBlock { Text = subtitle, Foreground = Res("Text3"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
    }

    private void AddSection(Panel target, string title, bool first = false)
    {
        var g = new Grid { Margin = new Thickness(0, first ? 4 : 18, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = (Style)FindResource("Micro"), Margin = new Thickness(0, 0, 10, 0) });
        var line = new System.Windows.Shapes.Rectangle { Height = 1, Fill = Res("Line"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(line, 1);
        g.Children.Add(line);
        target.Children.Add(g);
    }

    private void AddKeyValue(Panel target, string key, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = key, Foreground = Res("Text3"), FontSize = 11.5 });
        // Read-only TextBox so paths and names can be selected and copied.
        var v = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Res("Text"), Cursor = Cursors.IBeam };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        target.Children.Add(g);
    }

    private void AddNote(Panel target, string text, Brush? brush = null) =>
        target.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2), FontSize = 11.5, Foreground = brush ?? Res("Text3") });

    private Grid FieldRow(string caption, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = caption, Foreground = Res("Text3"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        PropertiesPanel.Children.Add(g);
        return g;
    }

    private void TextRow(string caption, string value, Action<string> commit)
    {
        var box = new TextBox { Text = value };
        void Apply() { if (box.Text != value) { RecordUndo($"rename"); value = box.Text; commit(box.Text); MarkDirty(); } }
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        FieldRow(caption, box);
    }

    private void ComboRow(string caption, IEnumerable<string> options, string value, Action<string> commit)
    {
        var combo = new ComboBox { ItemsSource = options.ToList(), SelectedItem = value };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s && s != value) { RecordUndo(caption.ToLowerInvariant()); value = s; commit(s); MarkDirty(); } };
        FieldRow(caption, combo);
    }

    private static readonly string[] AxisNames = ["X", "Y", "Z"];
    private static readonly string[] AxisBrushes = ["AxisX", "AxisY", "AxisZ"];

    /// <summary>Three numeric fields with coloured axis tags. Dragging a tag scrubs the value (Shift ×10, Alt ×0.1).</summary>
    private void Vec3Row(string caption, string unit, N.Vector3 value, Action<N.Vector3> set, Action refresh, double scrubStep, N.Vector3? reset)
    {
        var header = new Grid { Margin = new Thickness(0, 6, 0, 3) };
        header.Children.Add(new TextBlock { Text = $"{caption}", Foreground = Res("Text2"), FontSize = 11.5 });
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new TextBlock { Text = unit, Foreground = Res("Text4"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(right);
        PropertiesPanel.Children.Add(header);

        var grid = new Grid();
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var boxes = new TextBox[3];
        void Apply()
        {
            if (!TryF(boxes[0].Text, out var x) || !TryF(boxes[1].Text, out var y) || !TryF(boxes[2].Text, out var z)) return;
            var v = new N.Vector3(x, y, z);
            if (v == value) return;
            if (!_scrubbing) RecordUndo(caption.ToLowerInvariant());
            value = v;
            set(v);
            refresh();
            MarkDirty();
        }
        for (int i = 0; i < 3; i++)
        {
            int axis = i;
            float initial = axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
            var box = new TextBox { Text = initial.ToString("0.###", CultureInfo.InvariantCulture), Padding = new Thickness(17, 4, 3, 4), FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11.5 };
            box.LostFocus += (_, _) => Apply();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
            boxes[axis] = box;
            var tag = new Border
            {
                Width = 16, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1), Cursor = Cursors.SizeWE, Background = Brushes.Transparent,
                ToolTip = $"Drag to adjust {AxisNames[axis]} (Shift ×10, Alt ×0.1)",
                Child = new TextBlock { Text = AxisNames[axis], FontFamily = (FontFamily)FindResource("Display"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Res(AxisBrushes[axis]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            AttachScrub(tag, box, scrubStep, Apply);
            var cell = new Grid { Margin = new Thickness(axis == 0 ? 0 : 4, 0, 0, 0) };
            cell.Children.Add(box);
            cell.Children.Add(tag);
            Grid.SetColumn(cell, axis);
            grid.Children.Add(cell);
        }
        PropertiesPanel.Children.Add(grid);

        if (reset is { } r)
        {
            var btn = new Button { Style = (Style)FindResource("Ghost"), Content = "Reset", FontSize = 10.5, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), ToolTip = $"Reset {caption.ToLowerInvariant()}" };
            btn.Click += (_, _) =>
            {
                boxes[0].Text = r.X.ToString("0.###", CultureInfo.InvariantCulture);
                boxes[1].Text = r.Y.ToString("0.###", CultureInfo.InvariantCulture);
                boxes[2].Text = r.Z.ToString("0.###", CultureInfo.InvariantCulture);
                Apply();
            };
            right.Children.Insert(0, btn);
        }
    }

    private void ScalarRow(string caption, string unit, float value, Action<float> set, double scrubStep, string? hint)
    {
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.InvariantCulture), Padding = new Thickness(6, 4, 22, 4), FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11.5, ToolTip = hint };
        void Apply()
        {
            if (!TryF(box.Text, out var f) || f == value) return;
            if (!_scrubbing) RecordUndo(caption.ToLowerInvariant());
            value = f;
            set(f);
            MarkDirty();
        }
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        var cell = new Grid();
        cell.Children.Add(box);
        var u = new Border
        {
            Width = 20, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.SizeWE, Background = Brushes.Transparent, ToolTip = "Drag to adjust (Shift ×10, Alt ×0.1)",
            Child = new TextBlock { Text = unit, Foreground = Res("Text4"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        AttachScrub(u, box, scrubStep, Apply);
        cell.Children.Add(u);
        FieldRow(caption, cell);
    }

    /// <summary>Horizontal drag on <paramref name="handle"/> changes the number in <paramref name="box"/> live.</summary>
    private void AttachScrub(FrameworkElement handle, TextBox box, double step, Action apply)
    {
        double startX = 0, startValue = 0;
        bool dragging = false;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (!TryF(box.Text, out var v)) return;
            RecordUndo("adjust value"); // one undo step per scrub gesture
            _scrubbing = true;
            startValue = v; startX = e.GetPosition(this).X; dragging = true;
            handle.CaptureMouse(); e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            double k = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 0.1 : 1;
            double v = startValue + (e.GetPosition(this).X - startX) * step * k;
            box.Text = v.ToString("0.###", CultureInfo.InvariantCulture);
            apply();
        };
        handle.MouseLeftButtonUp += (_, _) => { if (!dragging) return; dragging = false; _scrubbing = false; handle.ReleaseMouseCapture(); };
    }

    private static bool TryF(string s, out float f) => float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out f) && float.IsFinite(f);
}
