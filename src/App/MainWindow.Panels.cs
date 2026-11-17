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
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
    private HashSet<string> _collapsedSections = new(StringComparer.Ordinal);
    private bool _showingNothing;

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>A 16 px stroke icon from the theme's I.* geometries.</summary>
    private System.Windows.Shapes.Path MakeIcon(string key, Brush? stroke = null, double scale = 1)
    {
        var p = new System.Windows.Shapes.Path { Style = (Style)FindResource("Icon"), Data = (Geometry)FindResource(key) };
        if (stroke is not null) p.Stroke = stroke;
        if (Math.Abs(scale - 1) > 0.01) p.LayoutTransform = new ScaleTransform(scale, scale);
        return p;
    }

    private static string CategoryIcon(string category) => category switch
    {
        SceneCategories.Buildings => "I.Building",
        SceneCategories.Vegetation => "I.Leaf",
        Core.Assets.AssetGroups.Characters => "I.User",
        Core.Assets.AssetGroups.Weapons => "I.Crosshair",
        Core.Assets.AssetGroups.Vehicles => "I.Car",
        "Level" => "I.Globe",
        _ => "I.Cube",
    };

    // ---------------------------------------------------------------- outliner

    private string HierarchyFilter => HierarchySearch?.Text?.Trim() ?? "";

    private bool Matches(string name) => HierarchyFilter.Length == 0 || name.Contains(HierarchyFilter, StringComparison.OrdinalIgnoreCase);

    private void OnHierarchyFilterChanged(object sender, TextChangedEventArgs e)
    {
        HierarchySearchHint.Visibility = HierarchySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildHierarchy();
    }

    private void RebuildHierarchy()
    {
        _syncingTree = true;
        try
        {
            Hierarchy.Items.Clear();
            bool filtering = HierarchyFilter.Length > 0;
            var root = Group("scene", SceneDisplayName, null, "I.Layers", top: true);

            var world = Group("world", "World", _maps.Count, "I.Globe");
            foreach (var m in _maps.Where(m => Matches(Path.GetFileNameWithoutExtension(m.Ref.Path))))
                world.Items.Add(Leaf(Path.GetFileNameWithoutExtension(m.Ref.Path), $"{m.Instances.Count:N0} · locked", m, "I.Map", Res("Text3")));
            AddGroup(root, world, filtering);

            foreach (var cat in SceneCategories.Placeable)
            {
                var objs = _doc.Objects.Where(o => o.Category == cat).ToList();
                var node = Group("cat." + cat, cat, objs.Count, CategoryIcon(cat));
                foreach (var o in objs.Where(o => Matches(o.Name)).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                    node.Items.Add(Leaf(o.Name, null, o, CategoryIcon(cat), Res("Text3")));
                AddGroup(root, node, filtering);
            }

            var gameplay = Group("gameplay", "Gameplay", null, "I.Flag");
            var spawns = Group("spawns", "Spawns", _doc.Spawns.Count, "I.Spawn");
            foreach (Team team in Enum.GetValues<Team>())
            {
                var list = _doc.Spawns.Where(s => s.Team == team).ToList();
                var swatch = Res(team == Team.WLF ? "TeamWlf" : "TeamScar");
                var t = Group("team." + team, team == Team.WLF ? "WLF" : "Seraphites", list.Count, "I.Flag", swatch);
                foreach (var s in list.Where(s => Matches(s.Name))) t.Items.Add(Leaf(s.Name, null, s, "I.Spawn", swatch));
                AddGroup(spawns, t, filtering);
            }
            AddGroup(gameplay, spawns, filtering);
            var volumes = Group("volumes", "Volumes", _doc.Boundary.Points.Count > 0 ? 1 : 0, "I.Volume");
            if (Matches("Arena boundary"))
                volumes.Items.Add(Leaf("Arena boundary", $"{_doc.Boundary.Points.Count} pts", Boundary, "I.Boundary", Res("Warning")));
            AddGroup(gameplay, volumes, filtering);
            AddGroup(root, gameplay, filtering);

            Hierarchy.Items.Add(root);
            OutlinerCount.Text = $"{_doc.Objects.Count} obj · {_doc.Spawns.Count} spawn";
        }
        finally { _syncingTree = false; }
        UpdateEmptyState();
        if (_selected is null && _showingNothing) ShowProperties();
        UpdateSelectionStatus();
        RefreshWorkspacePanel();
    }

    /// <summary>Adds a group under its parent; while filtering, groups without matches are left out.</summary>
    private static void AddGroup(ItemsControl parent, TreeViewItem group, bool filtering)
    {
        if (!filtering || group.Items.Count > 0) parent.Items.Add(group);
    }

    private TreeViewItem Group(string id, string title, int? count, string icon, Brush? iconBrush = null, bool top = false)
    {
        var header = new DockPanel { LastChildFill = true };
        if (count is int n)
        {
            var c = new TextBlock { Text = n.ToString(CultureInfo.InvariantCulture), Style = (Style)FindResource("Count") };
            DockPanel.SetDock(c, Dock.Right);
            header.Children.Add(c);
        }
        var ic = MakeIcon(icon, iconBrush ?? Res(top ? "AccentHi" : "Text3"));
        ic.Margin = new Thickness(0, 0, 6, 0);
        header.Children.Add(ic);
        header.Children.Add(new TextBlock
        {
            Text = title, FontWeight = top ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12,
            Foreground = Res(top ? "Text" : "Text2"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var item = new TreeViewItem { Header = header, IsExpanded = HierarchyFilter.Length > 0 || !_collapsedGroups.Contains(id), Focusable = false };
        item.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) _collapsedGroups.Remove(id); };
        item.Collapsed += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) _collapsedGroups.Add(id); };
        return item;
    }

    private TreeViewItem Leaf(string title, string? meta, object tag, string icon, Brush iconBrush)
    {
        var header = new DockPanel { LastChildFill = true };
        if (meta is not null)
        {
            var m = new TextBlock { Text = meta, FontSize = 10.5, Foreground = Res("Text4"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(m, Dock.Right);
            header.Children.Add(m);
        }
        var ic = MakeIcon(icon, iconBrush);
        ic.Margin = new Thickness(0, 0, 6, 0);
        header.Children.Add(ic);
        header.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var item = new TreeViewItem { Header = header, Tag = tag, IsExpanded = true, ToolTip = title };
        if (ReferenceEquals(tag, _selected) || (tag is BoundaryNode && _selected is BoundaryNode)) item.IsSelected = true;
        item.ContextMenu = LeafMenu(tag);
        // Right click selects the row first, so the context menu acts on it.
        item.PreviewMouseRightButtonDown += (_, e) => { item.IsSelected = true; e.Handled = false; };
        return item;
    }

    private ContextMenu? LeafMenu(object tag)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string? icon, Action run, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            if (icon is not null) mi.Icon = MakeIcon(icon);
            mi.Click += (_, _) => run();
            menu.Items.Add(mi);
            return mi;
        }
        string G(string id) => _bindings.Get(id).IsEmpty ? "" : _bindings.Get(id).Display.Replace(" + ", "+");
        switch (tag)
        {
            case SceneObject or SpawnPoint:
                Item("Focus", "I.Crosshair", () => OnFocus(null, null), G("view.focus"));
                Item("Duplicate", "I.Copy", () => OnDuplicate(null, null), G("edit.duplicate"));
                menu.Items.Add(new Separator());
                Item("Delete", "I.Trash", () => OnDelete(null, null), G("edit.delete"));
                break;
            case BoundaryNode:
                Item(ToolBoundary.IsChecked == true ? "Finish drawing" : "Draw points", "I.Boundary", () => SetBoundaryTool(ToolBoundary.IsChecked != true), G("tool.boundary"));
                Item("Clear boundary", "I.Trash", () => OnClearBoundary(this, new RoutedEventArgs()));
                break;
            case LoadedMap:
                Item("Frame everything", "I.Frame", () => OnFrameAll(null, null), G("view.frameAll"));
                break;
            default: return null;
        }
        return menu;
    }

    private void OnHierarchySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTree || e.NewValue is not TreeViewItem { Tag: { } tag }) return;
        if (tag is LoadedMap map) { Select(null); ShowMapProperties(map); return; }
        Select(tag);
    }

    private void OnHierarchyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Hierarchy.SelectedItem is not TreeViewItem { Tag: { } tag }) return;
        if (tag is SceneObject or SpawnPoint) { OnFocus(null, null); e.Handled = true; }
        else if (tag is LoadedMap) { OnFrameAll(null, null); e.Handled = true; }
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

    private const string NothingSelected = "Nothing selected";

    private void ShowProperties()
    {
        PropertiesPanel.Children.Clear();
        _showingNothing = false;
        switch (_selected)
        {
            case SceneObject o: ShowObjectProperties(o); break;
            case SpawnPoint s: ShowSpawnProperties(s); break;
            case BoundaryNode: ShowBoundaryProperties(); break;
            default:
                _showingNothing = true;
                Heading(NothingSelected, "Click an object in the viewport or the hierarchy.", "I.Select");
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
        Heading(o.Name, o.Prototype is null ? $"Actor · {o.Category}" : $"Map prototype · {o.Category}", CategoryIcon(o.Category));
        ActionRow(spawnOrObject: true);

        AddSection(PropertiesPanel, "Object");
        TextRow("Name", o.Name, v => { o.Name = v; RebuildHierarchy(); });
        AddKeyValue(Body(PropertiesPanel), "Type", o.Prototype is null ? "Actor pak" : "Map prototype");
        ComboRow("Category", SceneCategories.Placeable, o.Category, v => { o.Category = v; RebuildHierarchy(); });

        AddSection(PropertiesPanel, "Transform");
        void Refresh() { UpdateObjectTransform(o); UpdateSelectionVisuals(); }
        Vec3Row("Position", "m", o.Position, v => o.Position = v, Refresh, 0.05, null);
        Vec3Row("Rotation", "°", o.RotationDegrees, v => o.RotationDegrees = v, Refresh, 0.5, N.Vector3.Zero);
        Vec3Row("Scale", "×", o.Scale, v => o.Scale = N.Vector3.Max(v, new N.Vector3(0.01f)), Refresh, 0.01, N.Vector3.One);
        Inside(o.Position);

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
            foreach (var t in tex) TextureRow(Body(PropertiesPanel), pak, t);
            AddNote(PropertiesPanel, "Read-only: materials come from the pak and cannot be overridden yet.");

            AddSection(PropertiesPanel, "Collision");
            AddNote(PropertiesPanel, pak.HasCollision
                ? "The pak carries Havok (hknp) collision data. Not decoded or editable yet."
                : (o.Prototype is null ? "None in this pak." : "Background collision lives in the level's -phys pak (not decoded yet)."));
        }
        else AddNote(PropertiesPanel, "Mesh still loading, or the asset could not be decoded (shown as a red box).");

        var m = o.EngineMatrix();
        AddSection(PropertiesPanel, "Engine matrix");
        Body(PropertiesPanel).Children.Add(new TextBlock
        {
            Text = $"{m.M11,8:0.###} {m.M12,8:0.###} {m.M13,8:0.###}\n{m.M21,8:0.###} {m.M22,8:0.###} {m.M23,8:0.###}\n{m.M31,8:0.###} {m.M32,8:0.###} {m.M33,8:0.###}\n{m.M41,8:0.###} {m.M42,8:0.###} {m.M43,8:0.###}",
            FontFamily = (FontFamily)FindResource("Mono"), FontSize = 10.5, Foreground = Res("Text3"), Margin = new Thickness(0, 2, 0, 0),
            ToolTip = "Row vectors, translation in the last row (same convention as level instance records).",
        });
    }

    private void ShowSpawnProperties(SpawnPoint s)
    {
        Heading(s.Name, $"Spawn point · {s.Team}", "I.Spawn", Res(s.Team == Team.WLF ? "TeamWlf" : "TeamScar"));
        ActionRow(spawnOrObject: true);
        AddSection(PropertiesPanel, "Spawn");
        TextRow("Name", s.Name, v => { s.Name = v; RebuildHierarchy(); });
        ComboRow("Team", Enum.GetNames<Team>(), s.Team.ToString(), v => { s.Team = Enum.Parse<Team>(v); RebuildHelpers(); RebuildHierarchy(); ShowProperties(); });
        AddSection(PropertiesPanel, "Transform");
        Vec3Row("Position", "m", s.Position, v => s.Position = v, () => { RebuildHelpers(); RefreshGizmo(); }, 0.05, null);
        ScalarRow("Yaw", "°", s.YawDegrees, v => { s.YawDegrees = v; RebuildHelpers(); }, 0.5, "0 = +X, 90 = +Z");
        Inside(s.Position);
    }

    private void ShowBoundaryProperties()
    {
        var b = _doc.Boundary;
        Heading("Arena boundary", $"{b.Points.Count} points on the XZ plane", "I.Boundary", Res("Warning"));
        AddSection(PropertiesPanel, "Height range");
        ScalarRow("Min Y", "m", b.MinY, v => { b.MinY = v; RebuildHelpers(); }, 0.1, null);
        ScalarRow("Max Y", "m", b.MaxY, v => { b.MaxY = v; RebuildHelpers(); }, 0.1, null);
        AddSection(PropertiesPanel, "Points");
        AddNote(PropertiesPanel, "Use the Boundary tool (B) and click on the ground to add points.");
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var draw = new Button { Content = ToolBoundary.IsChecked == true ? "Finish drawing" : "Draw points", Margin = new Thickness(0, 0, 6, 6) };
        draw.Click += (_, _) => { SetBoundaryTool(ToolBoundary.IsChecked != true); ShowProperties(); };
        row.Children.Add(draw);
        if (b.Points.Count > 0)
        {
            var undo = new Button { Content = "Remove last point", Margin = new Thickness(0, 0, 6, 6) };
            undo.Click += (_, _) => { RecordUndo("remove boundary point"); b.Points.RemoveAt(b.Points.Count - 1); MarkDirty(); RebuildHelpers(); RebuildHierarchy(); ShowProperties(); };
            row.Children.Add(undo);
        }
        Body(PropertiesPanel).Children.Add(row);
        int outside = _doc.Spawns.Count(s => !b.Contains(s.Position));
        if (b.Points.Count >= 3)
            AddNote(PropertiesPanel, outside == 0 ? "All spawns are inside the arena." : $"⚠ {outside} spawn(s) are outside the arena.", outside == 0 ? Res("Success") : Res("Danger"));
    }

    private void ShowMapProperties(LoadedMap m)
    {
        PropertiesPanel.Children.Clear();
        _showingNothing = false;
        Heading(Path.GetFileNameWithoutExtension(m.Ref.Path), "Map · read-only background", "I.Map");
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

    /// <summary>Header of the inspector: type icon, name and subtitle.</summary>
    private void Heading(string title, string subtitle, string icon = "I.Cube", Brush? iconBrush = null)
    {
        var g = new Grid { Margin = new Thickness(12, 12, 12, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var tile = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(4), Background = Res("Bg3"), BorderBrush = Res("Line2"), BorderThickness = new Thickness(1),
            Child = MakeIcon(icon, iconBrush ?? Res("AccentHi"), 1.15), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        g.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Title"), FontSize = 14, ToolTip = title });
        text.Children.Add(new TextBlock { Text = subtitle, Foreground = Res("Text3"), FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        PropertiesPanel.Children.Add(g);
    }

    /// <summary>Plain group heading (Settings pages, shortcut list): title + hairline; rows follow it in the same panel.</summary>
    private void AddHeading(Panel target, string title)
    {
        var g = new Grid { Margin = new Thickness(0, target.Children.Count == 0 ? 6 : 22, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Res("Text2"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var line = new System.Windows.Shapes.Rectangle { Height = 1, Fill = Res("Line"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(line, 1);
        g.Children.Add(line);
        target.Children.Add(g);
    }

    /// <summary>Focus / duplicate / delete for the current selection (same handlers as the menus and shortcuts).</summary>
    private void ActionRow(bool spawnOrObject)
    {
        if (!spawnOrObject) return;
        var row = new UniformGrid3();
        Button B(string text, string icon, Action run, string tip)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var ic = MakeIcon(icon, null, 0.85);
            ic.Margin = new Thickness(0, 0, 5, 0);
            sp.Children.Add(ic);
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = sp, Padding = new Thickness(4, 4, 4, 4), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => run();
            return b;
        }
        row.Add(B("Focus", "I.Crosshair", () => OnFocus(null, null), "Frame the selection in the viewport"));
        row.Add(B("Duplicate", "I.Copy", () => OnDuplicate(null, null), "Duplicate the selection"));
        row.Add(B("Delete", "I.Trash", () => OnDelete(null, null), "Delete the selection (undo can restore it)"));
        row.Panel.Margin = new Thickness(12, 0, 8, 12);
        PropertiesPanel.Children.Add(row.Panel);
    }

    /// <summary>Three equal columns (UniformGrid without the primitives namespace clash).</summary>
    private sealed class UniformGrid3
    {
        public Grid Panel { get; } = new();
        public UniformGrid3() { for (int i = 0; i < 3; i++) Panel.ColumnDefinitions.Add(new ColumnDefinition()); }
        public void Add(UIElement e) { Grid.SetColumn(e, Panel.Children.Count); Panel.Children.Add(e); }
    }

    /// <summary>The body of the last section added to <paramref name="target"/> (or the target itself).</summary>
    private static Panel Body(Panel target) =>
        target.Children.Count > 0 && target.Children[^1] is Expander { Content: Panel body } ? body : target;

    /// <summary>A collapsible section; following rows for the same target go inside it. Collapsed state is remembered.</summary>
    private void AddSection(Panel target, string title, bool first = false)
    {
        var body = new StackPanel();
        var ex = new Expander { Header = title, Content = body, Style = (Style)FindResource("Section"), IsExpanded = !_collapsedSections.Contains(title) };
        if (first && target != PropertiesPanel) ex.Margin = new Thickness(0, 0, 0, -1);
        ex.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, ex) && _collapsedSections.Remove(title)) SaveCollapsedSections(); };
        ex.Collapsed += (_, e) => { if (ReferenceEquals(e.OriginalSource, ex) && _collapsedSections.Add(title)) SaveCollapsedSections(); };
        target.Children.Add(ex);
    }

    private void SaveCollapsedSections() => WriteSetting("inspector.collapsed", string.Join("\n", _collapsedSections));

    private void AddKeyValue(Panel target, string key, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = key, Foreground = Res("Text3"), FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0) });
        // Read-only TextBox so paths and names can be selected and copied.
        var v = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Res("Text"), Cursor = Cursors.IBeam };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        Body(target).Children.Add(g);
    }

    private void AddNote(Panel target, string text, Brush? brush = null) =>
        Body(target).Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2), FontSize = 11, Foreground = brush ?? Res("Text3") });

    /// <summary>Material texture: thumbnail, folder name and decode state.</summary>
    private void TextureRow(Panel target, Core.Assets.LoadedPak pak, string texture)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        bool decoded = pak.Textures.TryGetValue(texture, out var image);
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(2), Background = Res("Bg3"), BorderBrush = Res("Line2"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        if (decoded && image is not null) swatch.Background = new ImageBrush(TextureBitmap(pak, texture, image)) { Stretch = Stretch.UniformToFill };
        g.Children.Add(swatch);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(Path.GetDirectoryName(texture)) ?? texture, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = texture });
        text.Children.Add(new TextBlock { Text = decoded ? $"Decoded · {image!.Width}×{image.Height}" : "Not decoded", FontSize = 10.5, Foreground = Res(decoded ? "Text4" : "Danger") });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        target.Children.Add(g);
    }

    private Grid FieldRow(string caption, UIElement control, string? tip = null)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = caption, Foreground = Res("Text3"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        Body(PropertiesPanel).Children.Add(g);
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

    /// <summary>
    /// Label + three numeric fields with coloured axis tags on one row. Dragging a tag scrubs the value
    /// (Shift ×10, Alt ×0.1); the reset button restores the default.
    /// </summary>
    private void Vec3Row(string caption, string unit, N.Vector3 value, Action<N.Vector3> set, Action refresh, double scrubStep, N.Vector3? reset)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.Children.Add(new TextBlock { Text = caption, Foreground = Res("Text3"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, ToolTip = $"{caption} ({unit})" });

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
            var box = new TextBox { Text = initial.ToString("0.###", CultureInfo.InvariantCulture), Padding = new Thickness(15, 2, 2, 2), FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11, ToolTip = $"{caption} {AxisNames[axis]} ({unit})" };
            box.LostFocus += (_, _) => Apply();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
            boxes[axis] = box;
            var tag = new Border
            {
                Width = 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1), Cursor = Cursors.SizeWE, Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(2, 0, 0, 2),
                ToolTip = $"Drag to adjust {AxisNames[axis]} (Shift ×10, Alt ×0.1)",
                Child = new TextBlock { Text = AxisNames[axis], FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res(AxisBrushes[axis]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            AttachScrub(tag, box, scrubStep, Apply);
            var cell = new Grid { Margin = new Thickness(axis == 0 ? 0 : 3, 0, 0, 0) };
            cell.Children.Add(box);
            cell.Children.Add(tag);
            Grid.SetColumn(cell, axis + 1);
            grid.Children.Add(cell);
        }

        if (reset is { } r)
        {
            var btn = new Button { Style = (Style)FindResource("IconButton"), Width = 20, Height = 20, Content = MakeIcon("I.Undo", null, 0.75), ToolTip = $"Reset {caption.ToLowerInvariant()}", HorizontalAlignment = HorizontalAlignment.Right };
            btn.Click += (_, _) =>
            {
                boxes[0].Text = r.X.ToString("0.###", CultureInfo.InvariantCulture);
                boxes[1].Text = r.Y.ToString("0.###", CultureInfo.InvariantCulture);
                boxes[2].Text = r.Z.ToString("0.###", CultureInfo.InvariantCulture);
                Apply();
            };
            Grid.SetColumn(btn, 4);
            grid.Children.Add(btn);
        }
        Body(PropertiesPanel).Children.Add(grid);
    }

    private void ScalarRow(string caption, string unit, float value, Action<float> set, double scrubStep, string? hint)
    {
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.InvariantCulture), Padding = new Thickness(6, 2, 22, 2), FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11, ToolTip = hint };
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
        FieldRow(caption, cell, hint);
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
        handle.MouseEnter += (_, _) => { if (handle is Border b) b.Background = Res("Hover"); };
        handle.MouseLeave += (_, _) => { if (handle is Border b) b.Background = Brushes.Transparent; };
    }

    private static bool TryF(string s, out float f) => float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out f) && float.IsFinite(f);
}
