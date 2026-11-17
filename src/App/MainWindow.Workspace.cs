using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Pak;
using FactionsReborn.WorldEditor.Core.Scene;

namespace FactionsReborn.WorldEditor;

/// <summary>
/// Asset Workspace (docked under the viewport): the browser selection's preview card on the left and four tabs of cards.
/// Assets lists what the scene uses (click selects an instance, again cycles through them, double click focuses);
/// Materials, Textures and Meshes show the LOD0 content of the asset selected in the browser, as decoded from its pak.
/// Everything shown is read-only data from the game files.
/// </summary>
public partial class MainWindow
{
    private BrowserItem? _wsItem;
    private LoadedPak? _wsPak;

    // Decoded texture bitmaps for cards and inspector swatches (bounded; frozen so they can be shared).
    private readonly Dictionary<string, BitmapSource> _textureBitmaps = new(StringComparer.OrdinalIgnoreCase);

    private BitmapSource TextureBitmap(LoadedPak pak, string texture, TextureImage image)
    {
        string key = pak.Ref.Key + "|" + texture;
        if (_textureBitmaps.TryGetValue(key, out var bmp)) return bmp;
        if (_textureBitmaps.Count > 256) _textureBitmaps.Clear();
        bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bmp.Freeze();
        return _textureBitmaps[key] = bmp;
    }

    /// <summary>Called when the browser selection (or its decoded pak) changes.</summary>
    private void SetWorkspaceSubject(BrowserItem? item, LoadedPak? pak)
    {
        _wsItem = item;
        _wsPak = item is null ? null : pak;
        if (!WsTabAssets.IsChecked.GetValueOrDefault()) RefreshWorkspacePanel();
    }

    private void OnWorkspaceTabChanged(object sender, RoutedEventArgs e)
    {
        if (WsCards is null || _settings is null) return; // raised while the XAML is still loading
        WriteSetting("layout.workspaceTab", WsTabMaterials.IsChecked == true ? "materials" : WsTabTextures.IsChecked == true ? "textures" : WsTabMeshes.IsChecked == true ? "meshes" : "assets");
        RefreshWorkspacePanel();
    }

    private void RefreshWorkspacePanel()
    {
        if (WsCards is null || _settings is null || AssetWorkspace.Visibility != Visibility.Visible) return;
        WsCards.Children.Clear();
        string? emptyTitle = null, emptyText = null;
        if (WsTabAssets.IsChecked == true) BuildSceneAssetCards(ref emptyTitle, ref emptyText);
        else BuildPakCards(ref emptyTitle, ref emptyText);
        WsEmpty.Visibility = emptyTitle is null ? Visibility.Collapsed : Visibility.Visible;
        WsEmptyTitle.Text = emptyTitle ?? "";
        WsEmptyText.Text = emptyText ?? "";
    }

    // ---------------------------------------------------------------- scene assets

    private void BuildSceneAssetCards(ref string? emptyTitle, ref string? emptyText)
    {
        var groups = _doc.Objects.GroupBy(o => o.Asset.Key + "#" + o.Prototype).ToList();
        WsSubject.Text = groups.Count == 0 ? "" : $"{groups.Count} asset{(groups.Count == 1 ? "" : "s")} · {_doc.Objects.Count} instance{(_doc.Objects.Count == 1 ? "" : "s")} in the scene";
        if (groups.Count == 0)
        {
            emptyTitle = "No assets in the scene";
            emptyText = "Place assets from the browser (button, double click or drag into the viewport); they are listed here.";
            return;
        }
        string? selectedKey = _selected is SceneObject so ? so.Asset.Key + "#" + so.Prototype : null;
        foreach (var g in groups.OrderBy(g => g.First().Name, StringComparer.OrdinalIgnoreCase))
        {
            var first = g.First();
            var instances = g.ToList();
            string name = first.Prototype is null ? Path.GetFileNameWithoutExtension(first.Asset.Path) : Path.GetFileName(first.PrototypeName ?? first.Name);
            var thumb = SceneAssetThumbnail(g.Key, first);
            var card = Card(thumb, CategoryIcon(first.Category), name, $"{first.Category} · ×{instances.Count}",
                selected: g.Key == selectedKey,
                tooltip: $"{first.Asset.Path}\n{first.Asset.Archive}\n{instances.Count} instance(s) — click to select, click again for the next one, double click to focus");
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonDown += (_, e) =>
            {
                // Cycle through the instances of this asset.
                int at = _selected is SceneObject cur ? instances.IndexOf(cur) : -1;
                var next = instances[(at + 1) % instances.Count];
                Select(next);
                RebuildHierarchy();
                if (e.ClickCount == 2) OnFocus(null, null);
                e.Handled = true;
            };
            WsCards.Children.Add(card);
        }
    }

    /// <summary>The browser thumbnail when known, otherwise rendered once from the mesh the scene already built.</summary>
    private ImageSource? SceneAssetThumbnail(string key, SceneObject sample)
    {
        if (_thumbs.TryGetValue(key, out var t)) return t;
        if (!_modelCache.TryGetValue(key, out var model) || !_objectPaks.ContainsKey(sample.Id)) return null;
        var (min, max) = LocalBounds(sample);
        var img = RenderThumbnail(model, min, max);
        _thumbs[key] = img;
        return img;
    }

    // ---------------------------------------------------------------- materials / textures / meshes of the browser selection

    private void BuildPakCards(ref string? emptyTitle, ref string? emptyText)
    {
        var item = _wsItem;
        string what = WsTabMaterials.IsChecked == true ? "materials" : WsTabTextures.IsChecked == true ? "textures" : "meshes";
        WsSubject.Text = item?.Name ?? "";
        if (item is null) { emptyTitle = "No asset selected"; emptyText = $"Select an asset in the browser to inspect its {what}."; return; }
        if (item.IsLevel) { emptyTitle = "Levels are not inspected here"; emptyText = "Open the level, then browse its prototypes under \"Map pieces\"."; return; }
        var pak = _wsPak;
        if (pak is null)
        {
            emptyTitle = item.NoMesh ? "No mesh in this pak" : "Not decoded yet";
            emptyText = item.NoMesh ? "This pak holds animation, skeleton or data only." : _settings.Catalogue.AutoPreview ? "Decoding the asset…" : "Previews are off (Settings › Asset Catalogue); place or preview the asset to decode it.";
            return;
        }
        var geo = pak.Geometry;
        int[] subs = item.Prototype is int p && p < geo.Prototypes.Count ? geo.Prototypes[p].Lod0Submeshes : GeometryReader.ActorLod0(geo);

        if (WsTabMaterials.IsChecked == true)
        {
            var mats = subs.Select(s => geo.Submeshes[s].MaterialIndex).Distinct().ToList();
            WsSubject.Text = $"{item.Name} · {mats.Count} material{(mats.Count == 1 ? "" : "s")}";
            foreach (int mi in mats)
            {
                var mat = geo.Materials[mi];
                ImageSource? img = mat.BaseColorTexture is { } tex && pak.Textures.TryGetValue(tex, out var image) ? TextureBitmap(pak, tex, image) : null;
                int uses = subs.Count(s => geo.Submeshes[s].MaterialIndex == mi);
                string sub = mat.BaseColorTexture is null ? "no base colour" : img is null ? "texture not decoded" : $"{uses} submesh{(uses == 1 ? "" : "es")}";
                WsCards.Children.Add(Card(img, "I.Material", ShortName(mat.Name), sub, false, $"{mat.Name}\nBase colour: {mat.BaseColorTexture ?? "none"}"));
            }
            if (mats.Count == 0) { emptyTitle = "No materials"; emptyText = "The LOD0 of this asset references no material."; }
        }
        else if (WsTabTextures.IsChecked == true)
        {
            var textures = subs.Select(s => geo.Materials[geo.Submeshes[s].MaterialIndex].BaseColorTexture).OfType<string>().Distinct().ToList();
            WsSubject.Text = $"{item.Name} · {textures.Count} texture{(textures.Count == 1 ? "" : "s")}";
            foreach (var tex in textures)
            {
                bool decoded = pak.Textures.TryGetValue(tex, out var image);
                string folder = Path.GetFileName(Path.GetDirectoryName(tex)) ?? tex;
                WsCards.Children.Add(Card(decoded ? TextureBitmap(pak, tex, image!) : null, "I.Image", folder,
                    decoded ? $"{image!.Width}×{image.Height} · low mip" : "not decoded", false, tex, square: true));
            }
            if (textures.Count == 0) { emptyTitle = "No textures"; emptyText = "No base-colour texture is referenced by this asset's LOD0."; }
        }
        else
        {
            WsSubject.Text = $"{item.Name} · {subs.Length} LOD0 submesh{(subs.Length == 1 ? "" : "es")}";
            foreach (int si in subs)
            {
                var mesh = geo.Submeshes[si];
                var mat = geo.Materials[mesh.MaterialIndex];
                ImageSource? img = mat.BaseColorTexture is { } tex && pak.Textures.TryGetValue(tex, out var image) ? TextureBitmap(pak, tex, image) : null;
                var size = mesh.BoundsMax - mesh.BoundsMin;
                WsCards.Children.Add(Card(img, "I.Mesh", ShortName(mesh.Name), $"{mesh.Indices.Length / 3:N0} tris · {mesh.Positions.Length:N0} verts", false,
                    $"#{si} {mesh.Name}\nMaterial: {mat.Name}\nBounds: {size.X:0.##} × {size.Y:0.##} × {size.Z:0.##} m", iconOverlay: true));
            }
            if (subs.Length == 0) { emptyTitle = "No meshes"; emptyText = "This asset has no visible LOD0 submesh."; }
        }
    }

    private static string ShortName(string name)
    {
        var s = name.Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        return slash >= 0 && slash < s.Length - 1 ? s[(slash + 1)..] : s;
    }

    // ---------------------------------------------------------------- card

    /// <summary>A compact card: thumbnail (or icon), name and one line of detail, with hover and selected states.</summary>
    private Border Card(ImageSource? image, string icon, string title, string sub, bool selected, string tooltip, bool square = false, bool iconOverlay = false)
    {
        var imgHost = new Grid { Height = square ? 104 : 74 };
        var bg = new Border { Background = Res("Bg3"), CornerRadius = new CornerRadius(3) };
        imgHost.Children.Add(bg);
        if (image is not null)
            imgHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
                Opacity = iconOverlay ? 0.45 : 1,
            });
        if (image is null || iconOverlay) imgHost.Children.Add(MakeIcon(icon, Res(image is null ? "Text4" : "Text"), 1.6));

        var text = new StackPanel { Margin = new Thickness(2, 5, 2, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res(selected ? "Text" : "Text2") });
        text.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("Text4"), Margin = new Thickness(0, 1, 0, 0) });

        var content = new StackPanel();
        content.Children.Add(imgHost);
        content.Children.Add(text);
        var card = new Border
        {
            Width = 116, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(4, 4, 4, 6), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), Child = content, ToolTip = tooltip,
            Background = selected ? Res("AccentWash") : Res("Bg2"), BorderBrush = selected ? Res("Accent") : Res("Line"),
        };
        card.MouseEnter += (_, _) => { if (!selected) card.BorderBrush = Res("Line3"); };
        card.MouseLeave += (_, _) => { if (!selected) card.BorderBrush = Res("Line"); };
        return card;
    }
}
