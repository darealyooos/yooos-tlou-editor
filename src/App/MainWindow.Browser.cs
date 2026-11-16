using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Pak;
using FactionsReborn.WorldEditor.Core.Scene;
using FactionsReborn.WorldEditor.Rendering;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor;

/// <summary>An entry of the asset browser. <see cref="Category"/> is the scene category used on placement;
/// <see cref="Group"/> is the (name-based) browsing group shown as a chip.</summary>
public sealed class BrowserItem(string label, AssetRef asset, int? prototype, string category, AssetEntry? entry) : INotifyPropertyChanged
{
    public string Label { get; } = label;
    public AssetRef Asset { get; } = asset;
    public int? Prototype { get; } = prototype;
    public string Category { get; } = category;
    public AssetEntry? Entry { get; } = entry;

    public string Name { get; init; } = label;
    public string Detail { get; init; } = "";
    public string Group { get; init; } = category;
    public long Size { get; init; }
    public string Source { get; init; } = "";
    public bool IsLevel => Entry?.Kind == AssetKind.Level;
    public string Key => Asset.Key + "#" + Prototype;
    public string Initial => string.IsNullOrEmpty(Group) ? "?" : Group[..1];

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; Changed(); } }
    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set { _isFavorite = value; Changed(); } }
    private bool _noMesh;
    public bool NoMesh { get => _noMesh; set { _noMesh = value; Changed(); } }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public override string ToString() => Label;
}

/// <summary>One row of tiles in the grid view (rows keep the grid virtualised).</summary>
public sealed record BrowserRow(BrowserItem[] Items, int Columns);

public partial class MainWindow
{
    private const string AllGroup = "All";
    private static readonly string[] GroupOrder = AssetGroups.Order;

    private List<BrowserItem> _sourceItems = [];
    private string _sourceSignature = "";
    private List<BrowserItem> _shownItems = [];
    private BrowserItem? _browserSelection;
    private string _category = AllGroup;
    private bool _syncingList;
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private int _gridColumns = 2;

    private static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024):N0} KB";

    // ---------------------------------------------------------------- source + filtering

    private string CurrentSource => KindLevels.IsChecked == true ? "levels" : KindMapProtos.IsChecked == true ? "protos" : "actors";

    private void EnsureSourceItems()
    {
        string sig = $"{CurrentSource}|{_index.Count}|{string.Join(",", _maps.Select(m => m.Ref.Key))}";
        if (sig == _sourceSignature) return;
        _sourceSignature = sig;
        switch (CurrentSource)
        {
            case "protos":
                _sourceItems = _maps.SelectMany(m => m.Pak.Geometry.Prototypes.Select(p =>
                {
                    var cat = AssetIndex.GuessCategory(p.Name);
                    var mapName = Path.GetFileNameWithoutExtension(m.Ref.Path);
                    return new BrowserItem($"{Path.GetFileName(p.Name)}   [{mapName}]", m.Ref, p.Index, cat, null)
                    {
                        Name = Path.GetFileName(p.Name), Detail = $"{cat} · prototype #{p.Index} · {mapName}", Group = cat, Source = mapName,
                    };
                })).ToList();
                break;
            case "levels":
                _sourceItems = _index.Where(e => e.Kind == AssetKind.Level).Select(e => new BrowserItem(
                    $"{e.Name}   ({e.Size / 1024:N0} KB, {Path.GetFileNameWithoutExtension(e.Ref.Archive)})", e.Ref, null, e.Category, e)
                {
                    Name = e.Name, Detail = $"Level · {Size(e.Size)} · {Path.GetFileNameWithoutExtension(e.Ref.Archive)}", Group = "Level", Size = e.Size,
                    Source = Path.GetFileNameWithoutExtension(e.Ref.Archive),
                }).ToList();
                break;
            default:
                _sourceItems = _index.Where(e => e.Kind == AssetKind.Actor).Select(e =>
                {
                    var g = AssetGroups.Of(e.Name, e.Category);
                    return new BrowserItem($"{e.Name}   ({e.Size / 1024:N0} KB, {Path.GetFileNameWithoutExtension(e.Ref.Archive)})", e.Ref, null, e.Category, e)
                    {
                        Name = e.Name, Detail = $"{g} · {Size(e.Size)} · {Path.GetFileNameWithoutExtension(e.Ref.Archive)}", Group = g, Size = e.Size,
                        Source = Path.GetFileNameWithoutExtension(e.Ref.Archive),
                    };
                }).ToList();
                break;
        }
        foreach (var item in _sourceItems)
        {
            item.IsFavorite = _favorites.Contains(item.Key);
            if (_thumbs.TryGetValue(item.Key, out var t)) item.Thumbnail = t;
        }
        BuildCategoryChips();
    }

    private void BuildCategoryChips()
    {
        CategoryPanel.Children.Clear();
        if (CurrentSource == "levels") { CategoryPanel.Visibility = Visibility.Collapsed; _category = AllGroup; return; }
        CategoryPanel.Visibility = Visibility.Visible;
        var counts = _sourceItems.GroupBy(i => i.Group).ToDictionary(g => g.Key, g => g.Count());
        if (_category != AllGroup && !counts.ContainsKey(_category)) _category = AllGroup;
        AddChip(AllGroup, _sourceItems.Count);
        foreach (var g in GroupOrder.Concat(counts.Keys.Except(GroupOrder)))
            if (counts.TryGetValue(g, out int n)) AddChip(g, n);

        void AddChip(string group, int count)
        {
            var text = new TextBlock();
            text.Inlines.Add(new System.Windows.Documents.Run(group));
            text.Inlines.Add(new System.Windows.Documents.Run($"  {count:N0}") { Foreground = (Brush)FindResource("Text4"), FontSize = 10 });
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "Category", Content = text, Tag = group, IsChecked = group == _category };
            chip.Checked += (_, _) => { _category = group; RefreshAssetList(); };
            CategoryPanel.Children.Add(chip);
        }
    }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (AssetList is null) return;
        _category = AllGroup;
        RefreshAssetList();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, SortBox) && _settings is not null && SortBox.SelectedIndex >= 0 && _settings.Catalogue.DefaultSort != SortBox.SelectedIndex)
        {
            _settings.Catalogue.DefaultSort = SortBox.SelectedIndex;
            SaveSettingsSoon();
        }
        RefreshAssetList();
    }

    private void ApplySearchNow() => RefreshAssetList();

    private void OnClearSearch(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }

    private void RefreshAssetList()
    {
        if (AssetList is null || CategoryPanel is null) return;
        EnsureSourceItems();

        var words = (SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        FavOnlyGlyph.Text = FavOnly.IsChecked == true ? "" : "";

        IEnumerable<BrowserItem> q = _sourceItems;
        if (_category != AllGroup) q = q.Where(i => i.Group == _category);
        if (FavOnly.IsChecked == true) q = q.Where(i => i.IsFavorite);
        if (words.Length > 0) q = q.Where(i => words.All(w => i.Label.Contains(w, StringComparison.OrdinalIgnoreCase)));
        q = SortBox.SelectedIndex switch
        {
            1 => q.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
            2 => q.OrderByDescending(i => i.Size).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            3 => q.OrderBy(i => i.Size).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            4 => q.OrderBy(i => i.Source, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        _shownItems = q.ToList();

        _syncingList = true;
        try
        {
            AssetList.ItemsSource = _shownItems;
            if (_browserSelection is not null && _shownItems.Contains(_browserSelection)) AssetList.SelectedItem = _browserSelection;
        }
        finally { _syncingList = false; }
        RebuildGridRows();

        BrowserCount.Text = _shownItems.Count == _sourceItems.Count ? $"{_sourceItems.Count:N0}" : $"{_shownItems.Count:N0} / {_sourceItems.Count:N0}";
        UpdateBrowserEmptyState(words.Length > 0);
        UpdateBrowserButtons();
    }

    private void UpdateBrowserEmptyState(bool searching)
    {
        string? title = null, text = null;
        if (_library is null) { title = "Indexing"; text = "Reading the archive list of your TLOU Part II install…"; }
        else if (CurrentSource == "protos" && _maps.Count == 0) { title = "No map open"; text = "Open a level first: its prototypes (native buildings, walls, props) become placeable here."; }
        else if (_shownItems.Count == 0 && FavOnly.IsChecked == true && !searching) { title = "No favourites yet"; text = "Use the star on an asset's details to keep it here."; }
        else if (_shownItems.Count == 0) { title = "No matches"; text = "Try fewer words or another category."; }
        BrowserEmpty.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        BrowserEmptyTitle.Text = title ?? "";
        BrowserEmptyText.Text = text ?? "";
    }

    // ---------------------------------------------------------------- list / grid

    private void OnBrowserViewChanged(object sender, RoutedEventArgs e)
    {
        if (AssetList is null || AssetGrid is null) return;
        bool grid = ViewGrid.IsChecked == true;
        if (_settings is not null && _settings.Catalogue.DefaultView != (grid ? "grid" : "list"))
        {
            _settings.Catalogue.DefaultView = grid ? "grid" : "list";
            SaveSettingsSoon();
        }
        AssetList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        AssetGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        RebuildGridRows();
    }

    private void OnAssetGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AssetGrid is null || _settings is null) return;
        int cols = Math.Max(1, (int)((AssetGrid.ActualWidth - 22) / TileWidth));
        if (cols != _gridColumns) { _gridColumns = cols; RebuildGridRows(); }
    }

    private void RebuildGridRows()
    {
        if (AssetGrid is null || AssetGrid.Visibility != Visibility.Visible) { if (AssetGrid is not null) AssetGrid.ItemsSource = null; return; }
        int cols = Math.Max(1, _gridColumns);
        var rows = new List<BrowserRow>((_shownItems.Count + cols - 1) / cols);
        for (int i = 0; i < _shownItems.Count; i += cols)
            rows.Add(new BrowserRow(_shownItems.Skip(i).Take(cols).ToArray(), cols));
        AssetGrid.ItemsSource = rows;
    }

    private Point _tileDown;
    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserItem item) return;
        _tileDown = e.GetPosition(this);
        SetBrowserSelection(item);
        if (e.ClickCount == 2) ActivateBrowserItem(item);
        e.Handled = true;
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (sender as FrameworkElement)?.DataContext is not BrowserItem item || item.IsLevel) return;
        if ((e.GetPosition(this) - _tileDown).Length < 8) return;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(BrowserItem), item), DragDropEffects.Copy);
    }

    // ---------------------------------------------------------------- selection + actions

    private void OnAssetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList || AssetList.SelectedItem is not BrowserItem item) return;
        SetBrowserSelection(item);
    }

    private void SetBrowserSelection(BrowserItem? item)
    {
        if (ReferenceEquals(item, _browserSelection)) return;
        if (_browserSelection is not null) _browserSelection.IsSelected = false;
        _browserSelection = item;
        if (item is not null) item.IsSelected = true;
        if (!ReferenceEquals(AssetList.SelectedItem, item))
        {
            _syncingList = true;
            try { AssetList.SelectedItem = item; if (item is not null) AssetList.ScrollIntoView(item); }
            finally { _syncingList = false; }
        }
        ShowBrowserDetails(item, null);
        UpdateBrowserButtons();
        _ = LoadPreviewAsync(item);
    }

    private void UpdateBrowserButtons()
    {
        var item = _browserSelection;
        bool has = item is not null && _library is not null;
        PlaceButton.IsEnabled = CatPlaceButton.IsEnabled = has && !item!.IsLevel;
        OpenMapButton.IsEnabled = CatOpenMapButton.IsEnabled = has && item!.IsLevel;
        FavToggle.IsEnabled = CatFavButton.IsEnabled = item is not null;
        bool fav = item?.IsFavorite == true;
        FavToggle.IsChecked = fav;
        FavToggleGlyph.Text = fav ? "" : "";
        CatFavButton.Content = fav ? "Remove favourite" : "Add to favourites";
    }

    private async void OnAssetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only react to double clicks on an item, not on the scrollbar or empty space.
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(AssetList, d) is null) return;
        if (_browserSelection is { } item) await ActivateAsync(item);
    }

    private async void ActivateBrowserItem(BrowserItem item) => await ActivateAsync(item);

    private async Task ActivateAsync(BrowserItem item)
    {
        if (item.IsLevel) await OpenMapAsync(item.Asset);
        else await PlaceFromBrowserAsync(item);
    }

    /// <summary>Places at the point under the viewport centre; from the catalogue it switches to the World Editor first.</summary>
    private async Task PlaceFromBrowserAsync(BrowserItem item)
    {
        if (_library is null) return;
        if (_workspace != Workspace.World)
        {
            SetWorkspace(Workspace.World);
            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        await PlaceAsync(item, ViewCenterPoint());
    }

    private async void OnPlaceAsset(object sender, RoutedEventArgs e)
    {
        if (_browserSelection is { IsLevel: false } item) await PlaceFromBrowserAsync(item);
    }

    private async void OnOpenMap(object sender, RoutedEventArgs e)
    {
        if (_browserSelection is { IsLevel: true } item) await OpenMapAsync(item.Asset);
    }

    private void OnCataloguePlace(object sender, RoutedEventArgs e) => OnPlaceAsset(sender, e);

    private async void OnCatalogueOpenMap(object sender, RoutedEventArgs e)
    {
        if (_browserSelection is not { IsLevel: true } item) return;
        SetWorkspace(Workspace.World);
        await OpenMapAsync(item.Asset);
    }

    private void OnBrowseLevels(object sender, RoutedEventArgs e)
    {
        KindLevels.IsChecked = true;
        SearchBox.Focus();
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (_browserSelection is not { } item) return;
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite) _favorites.Add(item.Key); else _favorites.Remove(item.Key);
        WriteSetting("favorites", string.Join("\n", _favorites));
        UpdateBrowserButtons();
        if (FavOnly.IsChecked == true) RefreshAssetList();
    }

    // ---------------------------------------------------------------- drag to viewport

    private Point _dragStart;
    private void OnAssetDragStart(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = e.GetPosition(AssetList); return; }
        if ((e.GetPosition(AssetList) - _dragStart).Length < 8 || _browserSelection is not { IsLevel: false } item) return;
        if (e.OriginalSource is DependencyObject d && (d is Thumb || FindParent<ScrollBar>(d) is not null)) return;
        DragDrop.DoDragDrop(AssetList, new DataObject(typeof(BrowserItem), item), DragDropEffects.Copy);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void OnViewportDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(typeof(BrowserItem));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnViewportDragLeave(object sender, DragEventArgs e) => DropHighlight.Visibility = Visibility.Collapsed;

    private async void OnViewportDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(typeof(BrowserItem)) is BrowserItem item)
            await PlaceAsync(item, PickPoint(e.GetPosition(ViewportHost)));
    }

    // ---------------------------------------------------------------- details

    private void ShowBrowserDetails(BrowserItem? item, LoadedPak? pak)
    {
        CatalogueDetails.Children.Clear();
        if (item is null)
        {
            DetailName.Text = "No asset selected";
            AssetInfo.Text = _library is null ? "Waiting for asset index…" : "Select an asset to see its details.";
            CatKind.Text = "ASSET CATALOGUE";
            CatTitle.Text = "Select an asset";
            CatSubtitle.Text = "Browse on the left. Thumbnails and previews are decoded from your own game files.";
            return;
        }
        DetailName.Text = item.Name;
        DetailName.ToolTip = item.Name;
        CatKind.Text = (item.IsLevel ? "LEVEL" : item.Prototype is null ? "ACTOR" : "MAP PROTOTYPE") + "  ·  " + item.Group.ToUpperInvariant();
        CatTitle.Text = item.Name;
        CatSubtitle.Text = item.Asset.Path;

        var lines = new List<string> { item.Detail };
        var p = CatalogueDetails;
        AddSection(p, "Identity", first: true);
        AddKeyValue(p, "Name", item.Name);
        AddKeyValue(p, "Path", item.Asset.Path);
        AddKeyValue(p, "Archive", item.Asset.Archive);
        if (item.Size > 0) AddKeyValue(p, "Size", Size(item.Size));
        if (item.Prototype is int pi) AddKeyValue(p, "Prototype", $"#{pi}");
        if (!item.IsLevel)
        {
            AddKeyValue(p, "Browse group", item.Group);
            AddKeyValue(p, "Scene category", item.Category);
        }

        if (item.IsLevel)
        {
            var open = _maps.FirstOrDefault(m => m.Ref == item.Asset);
            AddSection(p, "Level");
            if (open is null)
                AddNote(p, "Open the level to load its instances, prototypes and textures. The map stays a read-only backdrop; place copies of its prototypes from \"Map pieces\".");
            else
            {
                AddKeyValue(p, "Instances", $"{open.Instances.Count:N0}");
                AddKeyValue(p, "Prototypes", $"{open.Pak.Geometry.Prototypes.Count:N0}");
                AddKeyValue(p, "LOD0 triangles", $"{open.Triangles:N0}");
                AddKeyValue(p, "Textures", $"{open.Pak.Textures.Count:N0} decoded");
                lines.Add($"Open · {open.Instances.Count:N0} instances");
            }
        }
        else if (pak is not null)
        {
            var geo = pak.Geometry;
            int[] subs = item.Prototype is int pr ? geo.Prototypes[pr].Lod0Submeshes : GeometryReader.ActorLod0(geo);
            int tris = subs.Sum(s => geo.Submeshes[s].Indices.Length / 3);
            var (min, max) = ProtoBounds(geo, item.Prototype);
            var size = max - min;
            var textures = subs.Select(s => geo.Materials[geo.Submeshes[s].MaterialIndex].BaseColorTexture).OfType<string>().Distinct().ToList();
            AddSection(p, "Mesh");
            AddKeyValue(p, "LOD0", $"{subs.Length} submeshes · {tris:N0} triangles");
            AddKeyValue(p, "Bounds", $"{size.X:0.##} × {size.Y:0.##} × {size.Z:0.##} m");
            AddSection(p, "Materials");
            if (textures.Count == 0) AddNote(p, "No base-colour texture referenced.");
            foreach (var t in textures)
                AddKeyValue(p, pak.Textures.ContainsKey(t) ? "Decoded" : "Not decoded", Path.GetFileName(Path.GetDirectoryName(t)) ?? t);
            AddNote(p, "Paks hold only low mips of textures; full resolution streams from .ndb and is not decoded.");
            AddSection(p, "Collision");
            AddNote(p, pak.HasCollision
                ? "Havok (hknp) collision data present. Not decoded or editable yet."
                : item.Prototype is null ? "None in this pak." : "Background collision lives in the level's -phys pak (not decoded yet).");
            lines.Add($"{subs.Length} submeshes · {tris:N0} tris · {size.X:0.#}×{size.Y:0.#}×{size.Z:0.#} m");
        }
        AssetInfo.Text = string.Join("\n", lines);
    }

    // ---------------------------------------------------------------- preview

    private Viewport3D _preview = null!;
    private readonly PerspectiveCamera _previewCam = new() { FieldOfView = 35, NearPlaneDistance = 0.02, FarPlaneDistance = 5000 };
    private readonly ModelVisual3D _previewModel = new();
    private double _pvYaw = -135, _pvPitch = -22, _pvDistance = 5;
    private Point3D _pvCenter;
    private int _previewToken;
    private Point _pvLast;
    private bool _pvDragging;

    private void SetupPreview()
    {
        _preview = new Viewport3D { Camera = _previewCam, ClipToBounds = true, IsHitTestVisible = false };
        _preview.Children.Add(new ModelVisual3D { Content = PreviewLights() });
        _preview.Children.Add(_previewModel);
        PreviewHostCompact.Child = _preview;
        foreach (var host in new[] { PreviewHostCompact, PreviewHostLarge })
        {
            host.MouseLeftButtonDown += (_, e) => { _pvDragging = true; _pvLast = e.GetPosition(host); host.CaptureMouse(); };
            host.MouseLeftButtonUp += (_, _) => { _pvDragging = false; host.ReleaseMouseCapture(); };
            host.MouseMove += (_, e) =>
            {
                if (!_pvDragging) return;
                var pos = e.GetPosition(host);
                _pvYaw += (pos.X - _pvLast.X) * 0.5;
                _pvPitch = Math.Clamp(_pvPitch - (pos.Y - _pvLast.Y) * 0.4, -85, 85);
                _pvLast = pos;
                ApplyPreviewCamera();
            };
            host.MouseWheel += (_, e) => { _pvDistance = Math.Clamp(_pvDistance * Math.Pow(0.999, e.Delta), 0.2, 4000); ApplyPreviewCamera(); };
        }
    }

    private static Model3DGroup PreviewLights() => ThumbnailRenderer.Lights();

    /// <summary>Moves the single preview viewport between the compact card and the catalogue stage.</summary>
    private void PlacePreview(bool large)
    {
        var target = large ? PreviewHostLarge : PreviewHostCompact;
        if (ReferenceEquals(target.Child, _preview)) return;
        PreviewHostCompact.Child = null;
        PreviewHostLarge.Child = null;
        target.Child = _preview;
    }

    private void ApplyPreviewCamera()
    {
        double y = _pvYaw * Math.PI / 180, p = _pvPitch * Math.PI / 180;
        var forward = new Vector3D(Math.Cos(p) * Math.Cos(y), Math.Sin(p), Math.Cos(p) * Math.Sin(y));
        _previewCam.Position = _pvCenter - forward * _pvDistance;
        _previewCam.LookDirection = forward;
        _previewCam.UpDirection = new Vector3D(0, 1, 0);
    }

    private static (N.Vector3 Min, N.Vector3 Max) ProtoBounds(GeometryData geo, int? prototype)
    {
        int[] subs = prototype is int p ? geo.Prototypes[p].Lod0Submeshes : GeometryReader.ActorLod0(geo);
        if (subs.Length == 0) return (N.Vector3.Zero, N.Vector3.One);
        return (subs.Select(s => geo.Submeshes[s].BoundsMin).Aggregate(N.Vector3.Min), subs.Select(s => geo.Submeshes[s].BoundsMax).Aggregate(N.Vector3.Max));
    }

    private void SetPreviewStatus(string text)
    {
        PreviewStatus.Text = text;
        CompactPreviewStatus.Text = _browserSelection is null ? "Select an asset to preview it" : text;
        PreviewHint.Visibility = _previewModel.Content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadPreviewAsync(BrowserItem? item, bool debounce = true)
    {
        int token = ++_previewToken;
        _previewModel.Content = null;
        if (item is null || _library is null) { SetPreviewStatus(""); return; }
        if (item.IsLevel) { SetPreviewStatus("Levels are previewed by opening them in the World Editor."); return; }
        if (!_settings.Catalogue.AutoPreview && debounce) { SetPreviewStatus("Previews are off (Settings › Asset Catalogue)."); return; }
        if (TryGetPreview(item.Key) is { } hit) { ShowPreview(item, hit.Model, hit.Min, hit.Max, _library.TryGetLoaded(item.Asset)); return; }
        SetPreviewStatus("Decoding…");
        if (debounce) await Task.Delay(140); // let fast keyboard scrolling settle before decoding
        if (token != _previewToken) return;
        try
        {
            // Decode without pinning: a pak already used by the scene is reused, anything else is decoded
            // into a temporary that is released when it leaves the bounded preview cache.
            LoadedPak? mapPak = item.Prototype is null ? null
                : _maps.FirstOrDefault(m => m.Ref == item.Asset)?.Pak ?? throw new InvalidOperationException("its map is no longer open");
            var lib = _library;
            bool textured = _settings.Performance.TexturedPreviews;
            var (pak, model) = await Task.Run(() =>
            {
                var p = mapPak ?? lib.TryGetLoaded(item.Asset) ?? AssetLibrary.Decode(item.Asset, lib.ReadRaw(item.Asset));
                return (p, MeshBuilder.BuildPrototype(p, item.Prototype, cacheMaterials: false, textured: textured));
            });
            if (token != _previewToken) return;
            var (min, max) = ProtoBounds(pak.Geometry, item.Prototype);
            AddPreview(item.Key, model, min, max);
            ShowPreview(item, model, min, max, pak);
            if (item.Thumbnail is null) StoreThumbnail(item, RenderThumbnail(model, min, max));
        }
        catch (Exception ex)
        {
            if (token != _previewToken) return;
            if (ex is InvalidDataException) MarkNoMesh(item); else _thumbFailed.Add(item.Key);
            SetPreviewStatus(ex is InvalidDataException ? "No mesh in this pak (animation, skeleton or data only)." : $"Cannot preview: {ex.Message}");
        }
    }

    private void ShowPreview(BrowserItem item, Model3DGroup model, N.Vector3 min, N.Vector3 max, LoadedPak? pak)
    {
        _previewModel.Content = model;
        _pvCenter = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        _pvDistance = Math.Max(0.5, (max - min).Length() * 1.8);
        _pvYaw = -135; _pvPitch = -22;
        ApplyPreviewCamera();
        SetPreviewStatus("");
        if (pak is not null) ShowBrowserDetails(item, pak);
    }

    // Bounded LRU of preview meshes (Settings > Performance). Details are kept with the decoded pak only while cached.
    private readonly LinkedList<(string Key, Model3DGroup Model, N.Vector3 Min, N.Vector3 Max)> _previewCache = new();

    private (Model3DGroup Model, N.Vector3 Min, N.Vector3 Max)? TryGetPreview(string key)
    {
        for (var n = _previewCache.First; n is not null; n = n.Next)
            if (n.Value.Key == key)
            {
                _previewCache.Remove(n);
                _previewCache.AddFirst(n);
                return (n.Value.Model, n.Value.Min, n.Value.Max);
            }
        return null;
    }

    private void AddPreview(string key, Model3DGroup model, N.Vector3 min, N.Vector3 max)
    {
        _previewCache.AddFirst((key, model, min, max));
        TrimPreviewCache();
    }

    private void TrimPreviewCache()
    {
        while (_previewCache.Count > _settings.Performance.PreviewCacheSize) _previewCache.RemoveLast();
    }

    // ---------------------------------------------------------------- thumbnails

    private const int ThumbSize = 112;
    private static string ThumbDir => Path.Combine(SettingsDir, "thumbs");
    private readonly Dictionary<string, ImageSource> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _thumbFailed = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<BrowserItem> _thumbQueue = new();
    private bool _thumbBusy;
    private CancellationTokenSource _thumbCancel = new();
    private bool AutoThumbnails => _settings.Performance.AutoThumbnails;

    // Loaded covers first realisation, DataContextChanged covers recycled containers.
    private void OnBrowserItemLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserItem item) EnsureThumbnail(item);
    }

    private void OnBrowserItemShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is BrowserItem item && (sender as FrameworkElement)?.IsLoaded == true) EnsureThumbnail(item);
    }

    private void EnsureThumbnail(BrowserItem item)
    {
        if (item.Thumbnail is not null || item.IsLevel || _library is null || _thumbFailed.Contains(item.Key)) return;
        if (_thumbs.TryGetValue(item.Key, out var cached)) { item.Thumbnail = cached; return; }
        var file = _settings.Performance.ThumbnailDiskCache ? ThumbFile(item) : null;
        if (file is not null && File.Exists(file))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                _thumbs[item.Key] = bmp;
                item.Thumbnail = bmp;
                return;
            }
            catch (Exception) { /* fall through and re-render */ }
        }
        if (!AutoThumbnails) return;
        _thumbQueue.Remove(item);
        _thumbQueue.AddLast(item);
        while (_thumbQueue.Count > 80) _thumbQueue.RemoveFirst(); // items that scrolled away long ago
        _ = PumpThumbnailsAsync();
    }

    private async Task PumpThumbnailsAsync()
    {
        if (_thumbBusy) return;
        _thumbBusy = true;
        var cancel = _thumbCancel.Token;
        try
        {
            while (_thumbQueue.Last is { } node && !cancel.IsCancellationRequested && _library is { } lib)
            {
                var item = node.Value;
                _thumbQueue.RemoveLast();
                if (item.Thumbnail is not null || _thumbFailed.Contains(item.Key)) continue;
                if (_thumbs.TryGetValue(item.Key, out var known)) { item.Thumbnail = known; continue; }
                LoadedPak? mapPak = item.Prototype is null ? null : _maps.FirstOrDefault(m => m.Ref == item.Asset)?.Pak;
                if (item.Prototype is not null && mapPak is null) continue;
                try
                {
                    var (model, min, max) = await Task.Run(() => BuildThumbnailModel(item, mapPak), cancel);
                    if (cancel.IsCancellationRequested) break;
                    StoreThumbnail(item, RenderThumbnail(model, min, max));
                }
                catch (OperationCanceledException) { break; }
                catch (InvalidDataException) { MarkNoMesh(item); }
                catch (Exception) { _thumbFailed.Add(item.Key); }
                await Task.Delay(10, CancellationToken.None); // keep the UI responsive between renders
            }
        }
        finally { _thumbBusy = false; }
    }

    private void MarkNoMesh(BrowserItem item)
    {
        _thumbFailed.Add(item.Key);
        foreach (var i in _sourceItems.Where(i => i.Key == item.Key)) i.NoMesh = true;
        item.NoMesh = true;
    }

    /// <summary>Uncached decode: browsing must not keep every visited pak in memory.</summary>
    private (Model3DGroup Model, N.Vector3 Min, N.Vector3 Max) BuildThumbnailModel(BrowserItem item, LoadedPak? mapPak)
    {
        var lib = _library ?? throw new InvalidOperationException("no game files");
        var pak = mapPak ?? (item.Prototype is null ? AssetLibrary.Decode(item.Asset, lib.ReadRaw(item.Asset))
            : _maps.FirstOrDefault(m => m.Ref == item.Asset)?.Pak ?? throw new InvalidOperationException("map closed"));
        var model = MeshBuilder.BuildPrototype(pak, item.Prototype, cacheMaterials: false, textured: _settings.Performance.TexturedPreviews);
        var (lo, hi) = ProtoBounds(pak.Geometry, item.Prototype);
        return (model, lo, hi);
    }

    private void StoreThumbnail(BrowserItem item, ImageSource image)
    {
        _thumbs[item.Key] = image;
        item.Thumbnail = image;
        foreach (var other in _sourceItems.Where(i => i.Key == item.Key && !ReferenceEquals(i, item))) other.Thumbnail = image;
        var file = _settings.Performance.ThumbnailDiskCache ? ThumbFile(item) : null;
        if (file is null || image is not BitmapSource bmp) return;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(ThumbDir);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(file);
                enc.Save(fs);
            }
            catch (Exception) { /* the cache is a convenience */ }
        });
    }

    /// <summary>Cache file per asset, invalidated when the source archive changes (size or date).</summary>
    private string? ThumbFile(BrowserItem item)
    {
        if (_buildDir is null) return null;
        try
        {
            var archive = new FileInfo(Path.Combine(_buildDir, item.Asset.Archive));
            var id = $"v1|{item.Key}|{archive.Length}|{archive.LastWriteTimeUtc.Ticks}";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(id)));
            return Path.Combine(ThumbDir, hash + ".png");
        }
        catch (Exception) { return null; }
    }

    private ImageSource RenderThumbnail(Model3DGroup model, N.Vector3 min, N.Vector3 max) =>
        ThumbnailRenderer.Render(model, min, max, ThumbSize, (Brush)FindResource("ViewportSky"));

    private void OnClearThumbCache(object sender, RoutedEventArgs e)
    {
        _thumbCancel.Cancel();
        _thumbCancel = new CancellationTokenSource();
        _thumbQueue.Clear();
        _thumbs.Clear();
        _thumbFailed.Clear();
        foreach (var i in _sourceItems) i.Thumbnail = null;
        int deleted = 0;
        try
        {
            if (Directory.Exists(ThumbDir))
                foreach (var f in Directory.GetFiles(ThumbDir, "*.png")) { File.Delete(f); deleted++; }
        }
        catch (Exception ex) { Status($"Thumbnail cache: {ex.Message}"); return; }
        Status($"Thumbnail cache cleared ({deleted} files).");
        if (AutoThumbnails) foreach (var i in _shownItems.Take(60)) EnsureThumbnail(i);
    }
}
