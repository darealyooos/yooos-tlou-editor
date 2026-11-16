using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Scene;
using FactionsReborn.WorldEditor.Rendering;
using Microsoft.Win32;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor;

/// <summary>A map (level pak) opened as the scene's background.</summary>
public sealed class LoadedMap
{
    public required AssetRef Ref { get; init; }
    public required LoadedPak Pak { get; init; }
    public required ModelVisual3D Solid { get; init; }
    public required ModelVisual3D Vegetation { get; init; }
    public required List<(int Prototype, N.Matrix4x4 World)> Instances { get; init; }
    public required N.Vector3 BoundsMin { get; init; }
    public required N.Vector3 BoundsMax { get; init; }
    public int Triangles { get; init; }
}

public partial class MainWindow : Window
{

    private string? _buildDir;
    private AssetLibrary? _library;
    private List<AssetEntry> _index = [];

    private SceneDocument _doc = new();
    private string? _docPath;
    private bool _dirty;

    private readonly List<LoadedMap> _maps = [];
    private readonly Dictionary<Guid, ModelVisual3D> _objectVisuals = [];
    private readonly Dictionary<Guid, LoadedPak> _objectPaks = [];
    private readonly Dictionary<string, Model3DGroup> _modelCache = [];

    private readonly CameraRig _camera = new();
    private readonly Gizmo _gizmo = new();
    private readonly ModelVisual3D _mapRoot = new(), _objectRoot = new(), _helperRoot = new(), _gridVisual = new(), _selectionVisual = new();
    private object? _selected;
    private int _pendingLoads;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        RegisterActions();
        SetupViewport();
        SetupPreview();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += async (_, _) => { RestoreLayout(); await InitialiseAsync(); };
        Closing += OnClosing;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        UpdateTitle();
        ShowProperties();
    }

    /// <summary>Scene to open once the asset index is ready (command line).</summary>
    public string? StartupScene { get; set; }
    /// <summary>When set, render the viewport to this PNG after loading and close (automated check).</summary>
    public string? SnapshotPath { get; set; }

    private async Task InitialiseAsync()
    {
        _buildDir = GameLocator.FindBuildDir(ReadSetting("buildDir"));
        if (_buildDir is null)
        {
            var dlg = new OpenFolderDialog { Title = "Select The Last of Us Part II install folder (or its build\\pc\\main)" };
            if (dlg.ShowDialog(this) == true) _buildDir = GameLocator.FindBuildDir(dlg.FolderName);
        }
        if (_buildDir is null)
        {
            AssetInfo.Text = "Game files not found. Settings › General › Game files, then restart.";
            Status("Game files not found: the editor needs your own TLOU Part II install.");
            UpdateBrowserEmptyState(false);
            return;
        }
        WriteSetting("buildDir", _buildDir);
        _library = new AssetLibrary(_buildDir);
        var progress = new Progress<string>(Status);
        _startupMs = SinceProcessStart.Elapsed.TotalMilliseconds;
        BeginLoad();
        var indexWatch = System.Diagnostics.Stopwatch.StartNew();
        try { _index = await Task.Run(() => AssetIndex.Scan(_buildDir, progress)); }
        finally { EndLoad(); }
        _indexMs = indexWatch.Elapsed.TotalMilliseconds;
        Status($"Asset index: {_index.Count(e => e.Kind == AssetKind.Actor)} actor paks, {_index.Count(e => e.Kind == AssetKind.Level)} level paks.");
        AssetInfo.Text = "Select an asset to see its details.";
        RefreshAssetList();
        RebuildHierarchy();
        // Automated runs always work in the World Editor workspace (the viewport must have a size).
        if (SelfTestPath is not null || SnapshotPath is not null) { SetWorkspace(Workspace.World); SetMaximised(false); UpdateLayout(); }

        if (ProfilePath is not null)
        {
            SetWorkspace(Workspace.World); SetMaximised(false); UpdateLayout();
            await RunProfileAsync(ProfilePath);
            _dirty = false;
            Application.Current.Shutdown(0);
            return;
        }
        if (StartupScene is null && SelfTestPath is null && SnapshotPath is null && _settings.General.Startup == "lastScene"
            && ReadSetting("lastScene") is { } last && File.Exists(last))
            StartupScene = last;
        if (StartupScene is not null) await OpenSceneAsync(StartupScene);
        if (SelfTestPath is not null)
        {
            await WhenIdleAsync();
            bool ok = await RunSelfTestAsync(SelfTestPath);
            if (SnapshotPath is not null) { await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); SaveViewportImage(SnapshotPath); }
            _dirty = false;
            Application.Current.Shutdown(ok ? 0 : 1);
            return;
        }
        if (SnapshotPath is not null)
        {
            await WhenIdleAsync();
            if (_doc.Camera is null) FrameAll();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            SaveViewportImage(SnapshotPath);
            Close();
        }
    }

    private async Task WhenIdleAsync()
    {
        while (_pendingLoads > 0) await Task.Delay(100);
    }

    private void BeginLoad() { _pendingLoads++; UpdateLoadIndicator(); }
    private void EndLoad() { _pendingLoads--; UpdateLoadIndicator(); }

    private void UpdateLoadIndicator()
    {
        if (LoadDot is null) return;
        LoadDot.Fill = (Brush)FindResource(_pendingLoads > 0 ? "Amber" : "Olive");
        LoadDot.ToolTip = _pendingLoads > 0 ? $"{_pendingLoads} load(s) in progress" : "Idle";
    }

    // ---------------------------------------------------------------- maps

    private async Task OpenMapAsync(AssetRef asset, bool frame = true)
    {
        if (_library is null) return;
        if (_maps.Any(m => m.Ref == asset)) { Status($"{asset.Path} is already open."); return; }
        BeginLoad();
        try
        {
            Status($"Loading map {asset.Path}…");
            var progress = new Progress<string>(Status);
            var lib = _library;
            var (pak, solid, veg, tris) = await Task.Run(() =>
            {
                var p = lib.Load(asset);
                var (s, v, t) = MeshBuilder.BuildLevel(p, progress);
                return (p, s, v, t);
            });
            var instances = pak.Geometry.Instances.Select(i => (i.Prototype, i.Matrix)).ToList();
            var (min, max) = InstanceBounds(pak, instances);
            var map = new LoadedMap
            {
                Ref = asset, Pak = pak, Instances = instances, BoundsMin = min, BoundsMax = max, Triangles = tris,
                Solid = new ModelVisual3D { Content = solid },
                Vegetation = new ModelVisual3D { Content = veg },
            };
            _maps.Add(map);
            _mapRoot.Children.Add(map.Solid);
            _mapRoot.Children.Add(map.Vegetation);
            ApplyLayerVisibility();
            if (!_doc.Maps.Contains(asset)) { _doc.Maps.Add(asset); MarkDirty(); }
            if (frame) _camera.Frame(new Point3D(min.X, min.Y, min.Z), new Point3D(max.X, max.Y, max.Z));
            Status($"Map {Path.GetFileNameWithoutExtension(asset.Path)}: {pak.Geometry.Instances.Count} instances, {pak.Geometry.Prototypes.Count} prototypes, {tris:N0} triangles, {pak.Textures.Count} textures.");
            RefreshAssetList();
            RebuildHierarchy();
        }
        catch (Exception ex)
        {
            Status($"Cannot open {asset.Path}: {ex.Message}");
        }
        finally { EndLoad(); }
    }

    private static (N.Vector3, N.Vector3) InstanceBounds(LoadedPak pak, List<(int Prototype, N.Matrix4x4 World)> instances) =>
        LevelBounds.Compute(pak.Geometry, instances);

    // ---------------------------------------------------------------- objects

    private async Task PlaceAsync(BrowserItem item, N.Vector3 at)
    {
        var obj = new SceneObject
        {
            Asset = item.Asset,
            Prototype = item.Prototype,
            Name = item.Prototype is null ? Path.GetFileNameWithoutExtension(item.Asset.Path) : Path.GetFileName(_maps.First(m => m.Ref == item.Asset).Pak.Geometry.Prototypes[item.Prototype.Value].Name),
            Category = item.Category,
            Position = at,
        };
        if (item.Prototype is int p)
            obj.PrototypeName = _maps.First(m => m.Ref == item.Asset).Pak.Geometry.Prototypes[p].Name;
        RecordUndo($"place {obj.Name}");
        _doc.Objects.Add(obj);
        MarkDirty();
        await CreateObjectVisualAsync(obj);
        Select(obj);
        RebuildHierarchy();
        Status($"Placed {obj.Name} at {Fmt(obj.Position)}.");
    }

    private async Task CreateObjectVisualAsync(SceneObject obj)
    {
        if (_library is null) return;
        var visual = new ModelVisual3D();
        _objectVisuals[obj.Id] = visual;
        _objectRoot.Children.Add(visual);
        UpdateObjectTransform(obj);
        BeginLoad();
        try
        {
            string key = obj.Asset.Key + "#" + obj.Prototype;
            var lib = _library;
            var pak = await Task.Run(() => lib.Load(obj.Asset));
            if (obj.Prototype is int p)
            {
                if (p >= pak.Geometry.Prototypes.Count || (obj.PrototypeName is not null && pak.Geometry.Prototypes[p].Name != obj.PrototypeName))
                    throw new InvalidDataException($"prototype #{p} is no longer '{obj.PrototypeName}' in {obj.Asset.Path}");
            }
            if (!_modelCache.TryGetValue(key, out var model))
            {
                model = await Task.Run(() => MeshBuilder.BuildPrototype(pak, obj.Prototype));
                _modelCache[key] = model;
            }
            _objectPaks[obj.Id] = pak;
            visual.Content = model;
        }
        catch (Exception ex)
        {
            visual.Content = Shapes.Model(Shapes.WireBox(new Point3D(-0.5, 0, -0.5), new Point3D(0.5, 1, 0.5), 0.03), Shapes.Emissive(Colors.Red));
            Status($"{obj.Name}: cannot load asset ({ex.Message}). Kept in the scene as a red box.");
        }
        finally { EndLoad(); }
        if (ReferenceEquals(_selected, obj)) UpdateSelectionVisuals();
    }

    private void UpdateObjectTransform(SceneObject obj)
    {
        if (_objectVisuals.TryGetValue(obj.Id, out var v)) v.Transform = new MatrixTransform3D(ToWpf(obj.EngineMatrix()));
    }

    private static Matrix3D ToWpf(N.Matrix4x4 m) =>
        new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

    private async void OnDuplicate(object? sender, RoutedEventArgs? e)
    {
        if (_selected is SceneObject or SpawnPoint) RecordUndo("duplicate");
        switch (_selected)
        {
            case SceneObject o:
                var copy = new SceneObject
                {
                    Asset = o.Asset, Prototype = o.Prototype, PrototypeName = o.PrototypeName, Name = o.Name + " copy", Category = o.Category,
                    Position = o.Position + new N.Vector3(1, 0, 1), RotationDegrees = o.RotationDegrees, Scale = o.Scale,
                };
                _doc.Objects.Add(copy);
                await CreateObjectVisualAsync(copy);
                Select(copy);
                break;
            case SpawnPoint s:
                var sp = new SpawnPoint { Team = s.Team, Name = NextSpawnName(s.Team), Position = s.Position + new N.Vector3(1, 0, 1), YawDegrees = s.YawDegrees };
                _doc.Spawns.Add(sp);
                Select(sp);
                break;
            default: return;
        }
        MarkDirty();
        RebuildHelpers();
        RebuildHierarchy();
    }

    private void OnDelete(object? sender, RoutedEventArgs? e)
    {
        if (_selected is not (SceneObject or SpawnPoint)) return;
        string name = _selected is SceneObject so ? so.Name : ((SpawnPoint)_selected).Name;
        if (_settings.General.ConfirmDelete && MessageBox.Show(this, $"Delete {name}? (Undo can restore it.)", "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        RecordUndo($"delete {name}");
        switch (_selected)
        {
            case SceneObject o:
                _doc.Objects.Remove(o);
                if (_objectVisuals.Remove(o.Id, out var v)) _objectRoot.Children.Remove(v);
                _objectPaks.Remove(o.Id);
                break;
            case SpawnPoint s:
                _doc.Spawns.Remove(s);
                break;
            default: return;
        }
        Select(null);
        MarkDirty();
        RebuildHelpers();
        RebuildHierarchy();
        TrimMemory(force: false);
    }

    private void OnAddSpawnWlf(object sender, RoutedEventArgs e) => AddSpawn(Team.WLF);
    private void OnAddSpawnScars(object sender, RoutedEventArgs e) => AddSpawn(Team.Seraphites);

    private void AddSpawn(Team team)
    {
        if (_workspace != Workspace.World) SetWorkspace(Workspace.World);
        var sp = new SpawnPoint { Team = team, Name = NextSpawnName(team), Position = ViewCenterPoint() };
        RecordUndo($"add {sp.Name}");
        _doc.Spawns.Add(sp);
        MarkDirty();
        RebuildHelpers();
        Select(sp);
        RebuildHierarchy();
    }

    private string NextSpawnName(Team team) => $"{team} spawn {_doc.Spawns.Count(s => s.Team == team) + 1}";

    private void OnClearBoundary(object sender, RoutedEventArgs e)
    {
        if (_doc.Boundary.Points.Count == 0) return;
        RecordUndo("clear boundary");
        _doc.Boundary.Points.Clear();
        MarkDirty();
        RebuildHelpers();
        RebuildHierarchy();
    }

    // ---------------------------------------------------------------- scene files

    private void OnNewScene(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        ResetScene();
        _doc = new SceneDocument();
        _docPath = null;
        _dirty = false;
        ClearHistory();
        TrimMemory(force: false);
        RebuildHelpers(); RebuildHierarchy(); UpdateTitle();
    }

    private void ResetScene()
    {
        Select(null);
        _maps.Clear();
        _mapRoot.Children.Clear();
        _objectRoot.Children.Clear();
        _objectVisuals.Clear();
        _objectPaks.Clear();
        RefreshAssetList();
    }

    private async void OnOpenScene(object? sender, RoutedEventArgs? e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "Factions Reborn scene (*.frscene)|*.frscene", InitialDirectory = ScenesDir() };
        if (dlg.ShowDialog(this) == true) await OpenSceneAsync(dlg.FileName);
    }

    private async Task OpenSceneAsync(string path)
    {
        SceneDocument doc;
        try { doc = SceneDocument.Load(path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open scene", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        ResetScene();
        _doc = doc;
        _docPath = path;
        foreach (var map in doc.Maps.ToList()) await OpenMapAsync(map, frame: doc.Camera is null);
        await Task.WhenAll(doc.Objects.Select(CreateObjectVisualAsync));
        if (doc.Camera is { } c) _camera.Set(new Point3D(c.Position.X, c.Position.Y, c.Position.Z), c.YawDegrees, c.PitchDegrees);
        _dirty = false;
        ClearHistory();
        TrimMemory(force: false);
        WriteSetting("lastScene", path);
        RebuildHelpers(); RebuildHierarchy(); UpdateTitle();
        Status($"Opened {Path.GetFileName(path)}: {doc.Maps.Count} map(s), {doc.Objects.Count} objects, {doc.Spawns.Count} spawns.");
    }

    private void OnSaveScene(object? sender, RoutedEventArgs? e) => SaveScene(_docPath);
    private void OnSaveSceneAs(object sender, RoutedEventArgs e) => SaveScene(null);

    private bool SaveScene(string? path)
    {
        if (path is null)
        {
            var dlg = new SaveFileDialog { Filter = "Factions Reborn scene (*.frscene)|*.frscene", InitialDirectory = ScenesDir(), FileName = _doc.Name + ".frscene" };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        _doc.Camera = new EditorCamera
        {
            Position = new N.Vector3((float)_camera.Position.X, (float)_camera.Position.Y, (float)_camera.Position.Z),
            YawDegrees = (float)_camera.Yaw, PitchDegrees = (float)_camera.Pitch,
        };
        try { _doc.Save(path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot save scene", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        _docPath = path;
        _dirty = false;
        WriteSetting("lastScene", path);
        UpdateTitle();
        Status($"Saved {path}");
        return true;
    }

    private string ScenesDir()
    {
        var dir = _settings.General.ScenesFolder is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Factions Reborn", "Scenes");
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status($"Scenes folder unavailable ({ex.Message})."); }
        return dir;
    }

    /// <summary>
    /// Releases decoded paks, meshes and materials no longer used by the scene (Settings › Performance).
    /// Previews never pin paks, so this only has to look at maps and placed objects.
    /// </summary>
    private void TrimMemory(bool force)
    {
        if (_library is null || (!force && !_settings.Performance.TrimUnusedPaks)) return;
        var keep = _maps.Select(m => m.Ref.Key).Concat(_doc.Objects.Select(o => o.Asset.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int paks = _library.Trim(keep);
        int mats = MeshBuilder.TrimMaterials(keep);
        foreach (var k in _modelCache.Keys.Where(k => !keep.Contains(k[..k.LastIndexOf('#')])).ToList()) _modelCache.Remove(k);
        if (force) { _previewCache.Clear(); Core.Archives.ChunkCache.Clear(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        if (force || paks > 0) Status($"Released {paks} pak(s) and {mats} material(s) not used by the scene.");
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty || !_settings.General.ConfirmDiscard) return true;
        var r = MessageBox.Show(this, "Save changes to the current scene?", "World Editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r == MessageBoxResult.No || (r == MessageBoxResult.Yes && SaveScene(_docPath));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool automated = SnapshotPath is not null || SelfTestPath is not null || ProfilePath is not null;
        if (!automated && !ConfirmDiscard()) { e.Cancel = true; return; }
        if (!automated) { SaveLayout(); _saveTimer?.Stop(); SaveSettingsNow(); }
        _thumbCancel.Cancel();
        _library?.Dispose();
    }

    private void MarkDirty() { if (!_dirty) { _dirty = true; UpdateTitle(); } }

    private string SceneDisplayName => _docPath is null ? _doc.Name : Path.GetFileNameWithoutExtension(_docPath);

    private void UpdateTitle()
    {
        Title = $"{SceneDisplayName}{(_dirty ? " •" : "")} — Yooos TLOU Editor";
        if (SceneNameText is null) return;
        SceneNameText.Text = SceneDisplayName;
        SceneNameText.ToolTip = _docPath ?? "Not saved yet";
        DirtyDot.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        StatusProject.Text = SceneDisplayName + (_dirty ? " (unsaved)" : "");
        StatusProject.ToolTip = _docPath ?? "Not saved yet";
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnSaveImage(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", InitialDirectory = ScenesDir(), FileName = _doc.Name + ".png" };
        if (dlg.ShowDialog(this) == true) SaveViewportImage(dlg.FileName);
    }

    private void SaveViewportImage(string path)
    {
        int w = Math.Max(1, (int)ViewportHost.ActualWidth), h = Math.Max(1, (int)ViewportHost.ActualHeight);
        // Render through a VisualBrush so the host's offset inside the window is not baked into the image.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(ViewportHost), null, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        Status($"Viewport saved to {path}");
    }

    // ---------------------------------------------------------------- misc

    private void Status(string text)
    {
        if (StatusText is null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess())
            StatusText.Text = text;
        else
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished && StatusText is not null)
                    StatusText.Text = text;
            }));
        // The full message stays reachable when the status bar trims it.
        if (Dispatcher.CheckAccess()) StatusText.ToolTip = text;
    }

    private static string Fmt(N.Vector3 v) => $"({v.X:0.##}, {v.Y:0.##}, {v.Z:0.##})";

}
