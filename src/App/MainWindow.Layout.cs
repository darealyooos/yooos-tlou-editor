using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FactionsReborn.WorldEditor.Core.Editor;

namespace FactionsReborn.WorldEditor;

public enum Workspace { Catalogue, World, Settings }

/// <summary>
/// Workspaces, docked panel layout (hierarchy / browser on the left, inspector on the right, asset workspace under
/// the viewport) and its persistence, the menu bar and the shortcut sheet.
/// </summary>
public partial class MainWindow
{
    private const double DefaultLeft = 300, DefaultRight = 320, DefaultBottom = 230, SplitGap = 3;
    private Workspace _workspace = Workspace.World;
    private bool _maximised;
    private bool _showWorkspacePanel = true;
    private GridLength _leftWidth = new(DefaultLeft), _rightWidth = new(DefaultRight), _bottomHeight = new(DefaultBottom);

    // ---------------------------------------------------------------- workspaces

    private void OnWorkspaceTab(object sender, RoutedEventArgs e)
    {
        if (CatalogueView is null || _preview is null || _settings is null) return; // raised while the XAML is still loading
        var ws = ReferenceEquals(sender, NavCatalogue) ? Workspace.Catalogue : ReferenceEquals(sender, NavSettings) ? Workspace.Settings : Workspace.World;
        if (ws != _workspace) SetWorkspace(ws);
    }

    private void SetWorkspace(Workspace ws)
    {
        bool leavingSettings = _workspace == Workspace.Settings && ws != Workspace.Settings;
        _workspace = ws;
        NavCatalogue.IsChecked = ws == Workspace.Catalogue;
        NavWorld.IsChecked = ws == Workspace.World;
        NavSettings.IsChecked = ws == Workspace.Settings;
        CatalogueView.Visibility = ws == Workspace.Catalogue ? Visibility.Visible : Visibility.Collapsed;
        WorldView.Visibility = ws == Workspace.World ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = ws == Workspace.Settings ? Visibility.Visible : Visibility.Collapsed;
        if (ws != Workspace.World && _maximised) SetMaximised(false);
        SetSidePanels();
        PlacePreview(ws == Workspace.Catalogue);
        if (ws == Workspace.Settings) { InitSettingsView(); BuildSettingsPage(); }
        if (leavingSettings) { CancelCapture(); _confirmResetAll = false; }
    }

    private void OnOpenPreferences(object sender, RoutedEventArgs e) => SetWorkspace(Workspace.Settings);

    /// <summary>Panels shown depend on the workspace, the maximise toggle, Appearance > Panels and View > Asset Workspace.</summary>
    private void SetSidePanels()
    {
        if (_settings is null) return;
        var a = _settings.Appearance;
        bool world = _workspace == Workspace.World;
        bool docked = !_maximised && _workspace != Workspace.Settings;
        bool showBrowser = docked && (a.ShowBrowser || _workspace == Workspace.Catalogue);
        bool showHierarchy = docked && world && a.ShowHierarchy;
        bool showLeft = showBrowser || showHierarchy;
        bool showInspector = docked && world && a.ShowInspector;
        bool showBottom = docked && world && _showWorkspacePanel;

        if (LeftColumn.Width.Value > 0) _leftWidth = LeftColumn.Width;
        if (RightColumn.Width.Value > 0) _rightWidth = RightColumn.Width;
        if (WorkspaceRow.Height.Value > 0) _bottomHeight = WorkspaceRow.Height;

        // Left: Scene Hierarchy over the Asset Browser.
        LeftColumn.MinWidth = showLeft ? 250 : 0;
        LeftColumn.Width = showLeft ? _leftWidth : new GridLength(0);
        LeftPanel.Visibility = LeftSplitter.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitColumn.Width = new GridLength(showLeft ? SplitGap : 0);
        OutlinerPanel.Visibility = showHierarchy ? Visibility.Visible : Visibility.Collapsed;
        BrowserPanel.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;
        LeftPanelSplitter.Visibility = showHierarchy && showBrowser ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitRow.Height = new GridLength(showHierarchy && showBrowser ? SplitGap : 0);
        OutlinerRow.MinHeight = showHierarchy ? 120 : 0;
        BrowserRow.MinHeight = showBrowser ? 220 : 0;
        if (!showHierarchy) OutlinerRow.Height = new GridLength(0);
        else if (OutlinerRow.Height.Value == 0) OutlinerRow.Height = new GridLength(_leftSplit, GridUnitType.Star);
        if (!showBrowser) BrowserRow.Height = new GridLength(0);
        else if (BrowserRow.Height.Value == 0) BrowserRow.Height = new GridLength(1 - _leftSplit, GridUnitType.Star);

        // Right: Inspector.
        RightColumn.MinWidth = showInspector ? 260 : 0;
        RightColumn.Width = showInspector ? _rightWidth : new GridLength(0);
        InspectorPanel.Visibility = RightSplitter.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        RightSplitColumn.Width = new GridLength(showInspector ? SplitGap : 0);

        // Bottom: Asset Workspace.
        WorkspaceRow.MinHeight = showBottom ? 140 : 0;
        WorkspaceRow.Height = showBottom ? _bottomHeight : new GridLength(0);
        WorkspaceSplitRow.Height = new GridLength(showBottom ? SplitGap : 0);
        AssetWorkspace.Visibility = WorkspaceSplitter.Visibility = showBottom ? Visibility.Visible : Visibility.Collapsed;
        MenuToggleWorkspace.IsChecked = _showWorkspacePanel;
        if (showBottom) RefreshWorkspacePanel();
    }

    private double _leftSplit = 0.4;

    private void SetMaximised(bool on)
    {
        if (on && _workspace != Workspace.World) SetWorkspace(Workspace.World);
        _maximised = on;
        MaximiseItem.IsChecked = on;
        MaximiseGlyph.Data = (Geometry)FindResource(on ? "I.Restore" : "I.Maximise");
        SetSidePanels();
    }

    private void OnToggleMaximise(object sender, RoutedEventArgs e) => SetMaximised(MaximiseItem.IsChecked);
    private void OnToggleMaximiseButton(object sender, RoutedEventArgs e) => SetMaximised(!_maximised);

    private void SetWorkspacePanel(bool show)
    {
        if (show && _workspace != Workspace.World) SetWorkspace(Workspace.World);
        if (show && _maximised) SetMaximised(false);
        _showWorkspacePanel = show;
        SetSidePanels();
        SaveLayout();
    }

    private void OnWorkspacePanelMenu(object sender, RoutedEventArgs e) => SetWorkspacePanel(MenuToggleWorkspace.IsChecked);
    private void OnHideWorkspacePanel(object sender, RoutedEventArgs e) => SetWorkspacePanel(false);

    // ---------------------------------------------------------------- layout persistence

    private void RestoreLayout()
    {
        if (TryReadDouble("layout.left", out var l)) _leftWidth = LeftColumn.Width = new GridLength(Math.Clamp(l, 250, 700));
        if (TryReadDouble("layout.right", out var r)) _rightWidth = RightColumn.Width = new GridLength(Math.Clamp(r, 260, 700));
        if (TryReadDouble("layout.bottom", out var b)) _bottomHeight = WorkspaceRow.Height = new GridLength(Math.Clamp(b, 140, 600));
        if (TryReadDouble("layout.leftSplit", out var o) && o is > 0.1 and < 0.9)
        {
            _leftSplit = o;
            OutlinerRow.Height = new GridLength(o, GridUnitType.Star);
            BrowserRow.Height = new GridLength(1 - o, GridUnitType.Star);
        }
        if (TryReadDouble("layout.categories", out var c0)) CategoryTreeRow.Height = new GridLength(Math.Clamp(c0, 48, 500));
        _showWorkspacePanel = ReadSetting("layout.workspacePanel") != "hidden";
        if (ReadSetting("layout.workspaceTab") is { } tab)
            (tab switch { "materials" => WsTabMaterials, "textures" => WsTabTextures, "meshes" => WsTabMeshes, _ => WsTabAssets }).IsChecked = true;
        var c = _settings.Catalogue;
        (c.DefaultView == "grid" ? ViewGrid : ViewList).IsChecked = true;
        SortBox.SelectedIndex = Math.Clamp(c.DefaultSort, 0, SortBox.Items.Count - 1);
        _favorites = new HashSet<string>((ReadSetting("favorites") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        _collapsedSections = new HashSet<string>((ReadSetting("inspector.collapsed") ?? "Engine matrix").Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        SetTool(_settings.Viewport.DefaultTool == "select" ? null : Rendering.GizmoMode.Move);
        ShowGrid.IsChecked = _settings.Viewport.ShowGrid;
        ApplyLayerVisibility();
        ApplySettings();
        SetWorkspace(ReadSetting("workspace") == "catalogue" ? Workspace.Catalogue : Workspace.World);
        RefreshShortcutLabels();
    }

    private void SaveLayout()
    {
        if (LeftColumn.Width.Value > 0) _leftWidth = LeftColumn.Width;
        if (RightColumn.Width.Value > 0) _rightWidth = RightColumn.Width;
        if (WorkspaceRow.Height.Value > 0) _bottomHeight = WorkspaceRow.Height;
        WriteSetting("layout.left", _leftWidth.Value.ToString(CultureInfo.InvariantCulture));
        WriteSetting("layout.right", _rightWidth.Value.ToString(CultureInfo.InvariantCulture));
        WriteSetting("layout.bottom", _bottomHeight.Value.ToString(CultureInfo.InvariantCulture));
        double total = OutlinerRow.ActualHeight + BrowserRow.ActualHeight;
        if (total > 0 && OutlinerRow.ActualHeight > 0 && BrowserRow.ActualHeight > 0)
        {
            _leftSplit = OutlinerRow.ActualHeight / total;
            WriteSetting("layout.leftSplit", _leftSplit.ToString("0.###", CultureInfo.InvariantCulture));
        }
        if (CategoryTreeRow.ActualHeight > 0) WriteSetting("layout.categories", CategoryTreeRow.ActualHeight.ToString("0", CultureInfo.InvariantCulture));
        WriteSetting("layout.workspacePanel", _showWorkspacePanel ? "shown" : "hidden");
        WriteSetting("workspace", _workspace == Workspace.Catalogue ? "catalogue" : "world");
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => SaveLayout();

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        _leftWidth = new GridLength(DefaultLeft);
        _rightWidth = new GridLength(DefaultRight);
        _bottomHeight = new GridLength(DefaultBottom);
        LeftColumn.Width = _leftWidth;
        RightColumn.Width = _rightWidth;
        WorkspaceRow.Height = _bottomHeight;
        _leftSplit = 0.4;
        OutlinerRow.Height = new GridLength(2, GridUnitType.Star);
        BrowserRow.Height = new GridLength(3, GridUnitType.Star);
        CategoryTreeRow.Height = new GridLength(150);
        _showWorkspacePanel = true;
        _settings.Appearance.ShowBrowser = _settings.Appearance.ShowHierarchy = _settings.Appearance.ShowInspector = true;
        SetMaximised(false);
        ApplySettings();
        SaveLayout();
        Status("Panel layout reset.");
    }

    private static bool TryReadDouble(string key, out double v) =>
        double.TryParse(ReadSetting(key), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    // ---------------------------------------------------------------- menu bar

    private void OnOpenNavMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 2;
        menu.IsOpen = true;
    }

    private void OnToolsMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuSelect.IsChecked = _tool is null;
        MenuMove.IsChecked = _tool == Rendering.GizmoMode.Move;
        MenuRotate.IsChecked = _tool == Rendering.GizmoMode.Rotate;
        MenuScale.IsChecked = _tool == Rendering.GizmoMode.Scale;
        MenuBoundary.IsChecked = ToolBoundary.IsChecked == true;
        MenuDuplicate.IsEnabled = MenuDeleteItem.IsEnabled = _selected is Core.Scene.SceneObject or Core.Scene.SpawnPoint;
        UpdateUndoMenu();
    }

    private void OnMenuBoundary(object sender, RoutedEventArgs e) => SetBoundaryTool(ToolBoundary.IsChecked != true);

    private void OnDeselect(object sender, RoutedEventArgs e) { Select(null); RebuildHierarchy(); }

    private void OnReleaseMemory(object sender, RoutedEventArgs e) => TrimMemory(force: true);

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            $"Yooos TLOU Editor {AppVersion}\n\nAsset catalogue and World Editor for The Last of Us Part II Remastered.\n" +
            "Independent community tool: it reads your own game files and never modifies them.\n\n" +
            $"Settings: {SettingsDir}",
            "About Yooos TLOU Editor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string AppVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (info is not null) return "v" + info.Split('+')[0];
            var v = asm.GetName().Version;
            return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private void OnLayerChip(object sender, RoutedEventArgs e)
    {
        ShowMap.IsChecked = ChipMap.IsChecked == true;
        ShowVegetation.IsChecked = ChipVegetation.IsChecked == true;
        ShowGrid.IsChecked = ChipGrid.IsChecked == true;
        ApplyLayerVisibility();
    }

    // ---------------------------------------------------------------- shortcut sheet (generated from the live bindings)

    private void BuildShortcutSheet()
    {
        ShortcutGrid.Children.Clear();
        var rows = ShortcutCatalog.All.Where(a => a.Context == ShortcutContext.Editor && !_bindings.Get(a.Id).IsEmpty)
            .Select(a => (Keys: _bindings.Get(a.Id).Display, What: a.Name)).ToList();
        rows.Add(("Hold right mouse", $"Fly: {string.Join(" ", new[] { "fly.forward", "fly.left", "fly.back", "fly.right", "fly.down", "fly.up" }.Select(id => KeyChord.KeyLabel(_bindings.Get(id).Key)))} · Shift faster"));
        rows.Add(("Alt + left drag", "Orbit · middle drag pans · wheel dollies (zooms in ortho)"));
        rows.Add(("Ctrl while dragging", "Snap 0.25 m · 15° · 0.1×"));
        rows.Add(("Esc", "Finish boundary, leave a field, or deselect"));
        int half = (rows.Count + 1) / 2;
        for (int col = 0; col < 2; col++)
        {
            var panel = new StackPanel();
            foreach (var (keys, what) in rows.Skip(col * half).Take(half))
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = keys, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11, Foreground = (Brush)FindResource("AccentHi"), TextWrapping = TextWrapping.Wrap });
                var t = new TextBlock { Text = what, FontSize = 11.5, Foreground = (Brush)FindResource("Text2"), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(t, 1);
                g.Children.Add(t);
                panel.Children.Add(g);
            }
            Grid.SetColumn(panel, col * 2);
            ShortcutGrid.Children.Add(panel);
        }
    }

    private void OnToggleShortcuts(object? sender, RoutedEventArgs? e)
    {
        if (_workspace != Workspace.World) SetWorkspace(Workspace.World);
        ShortcutSheet.Visibility = ShortcutSheet.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnShortcutSheetClick(object sender, MouseButtonEventArgs e) { ShortcutSheet.Visibility = Visibility.Collapsed; e.Handled = true; }

    /// <summary>When the viewport is too narrow for one toolbar row, the view controls wrap onto a second row.</summary>
    private void OnWorldViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ToolOverlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        LayerOverlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool fits = ToolOverlay.DesiredSize.Width + LayerOverlay.DesiredSize.Width + 24 <= WorldView.ActualWidth;
        Grid.SetRow(LayerOverlay, fits ? 0 : 1);
        Grid.SetColumn(LayerOverlay, fits ? 1 : 0);
        LayerOverlay.HorizontalAlignment = fits ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        LayerOverlay.Margin = new Thickness(0, fits ? 0 : 3, 0, 0);
    }

    // ---------------------------------------------------------------- empty state

    private void UpdateEmptyState()
    {
        if (EmptyState is null) return;
        bool empty = _maps.Count == 0 && _doc.Objects.Count == 0 && _doc.Spawns.Count == 0 && _doc.Boundary.Points.Count == 0 && _pendingLoads == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- dark title bar (Windows 10 20H1+ / 11)

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));            // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = 0x00231C19;                                         // COLORREF 0x00BBGGRR = #191C23 (Bg1)
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));        // DWMWA_CAPTION_COLOR (Windows 11)
            int text = 0x00EBE7E5;                                            // #E5E7EB (Text)
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));           // DWMWA_TEXT_COLOR
            int border = 0x00413530;                                          // #303541 (Line)
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));         // DWMWA_BORDER_COLOR
        }
        catch (Exception) { /* older Windows: keep the system title bar */ }
    }
}
