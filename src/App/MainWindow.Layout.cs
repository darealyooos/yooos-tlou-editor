using System.Globalization;
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

/// <summary>Workspaces, panel layout persistence, navigation menus and the shortcut sheet.</summary>
public partial class MainWindow
{
    private const double DefaultLeft = 300, DefaultRight = 320;
    private Workspace _workspace = Workspace.World;
    private bool _maximised;
    private GridLength _leftWidth = new(DefaultLeft), _rightWidth = new(DefaultRight);

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
        CompactDetail.Visibility = ws == Workspace.World ? Visibility.Visible : Visibility.Collapsed;
        if (ws != Workspace.World && _maximised) SetMaximised(false);
        SetSidePanels();
        PlacePreview(ws == Workspace.Catalogue);
        if (ws == Workspace.Settings) { InitSettingsView(); BuildSettingsPage(); }
        if (leavingSettings) { CancelCapture(); _confirmResetAll = false; }
    }

    /// <summary>Panels shown depend on the workspace, the maximise toggle and Appearance > Panels.</summary>
    private void SetSidePanels()
    {
        if (_settings is null) return;
        var a = _settings.Appearance;
        bool world = _workspace == Workspace.World;
        bool showLeft = !_maximised && _workspace != Workspace.Settings && (a.ShowBrowser || _workspace == Workspace.Catalogue);
        bool showHierarchy = world && !_maximised && a.ShowHierarchy;
        bool showInspector = world && !_maximised && a.ShowInspector;
        bool showRight = showHierarchy || showInspector;
        if (LeftColumn.Width.Value > 0) _leftWidth = LeftColumn.Width;
        if (RightColumn.Width.Value > 0) _rightWidth = RightColumn.Width;
        LeftColumn.MinWidth = showLeft ? 250 : 0;
        LeftColumn.Width = showLeft ? _leftWidth : new GridLength(0);
        BrowserPanel.Visibility = LeftSplitter.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitColumn.Width = new GridLength(showLeft ? 1 : 0);
        RightColumn.MinWidth = showRight ? 260 : 0;
        RightColumn.Width = showRight ? _rightWidth : new GridLength(0);
        ScenePanel.Visibility = RightSplitter.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        RightSplitColumn.Width = new GridLength(showRight ? 1 : 0);

        // Hierarchy / inspector split inside the right panel.
        OutlinerPanel.Visibility = showHierarchy ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        ScenePanelSplitter.Visibility = showHierarchy && showInspector ? Visibility.Visible : Visibility.Collapsed;
        OutlinerRow.MinHeight = showHierarchy ? 120 : 0;
        InspectorRow.MinHeight = showInspector ? 160 : 0;
        if (!showHierarchy) OutlinerRow.Height = new GridLength(0);
        else if (OutlinerRow.Height.Value == 0) OutlinerRow.Height = new GridLength(2, GridUnitType.Star);
        if (!showInspector) InspectorRow.Height = new GridLength(0);
        else if (InspectorRow.Height.Value == 0) InspectorRow.Height = new GridLength(3, GridUnitType.Star);
    }

    private void SetMaximised(bool on)
    {
        if (on && _workspace != Workspace.World) SetWorkspace(Workspace.World);
        _maximised = on;
        MaximiseItem.IsChecked = on;
        MaximiseGlyph.Text = on ? "" : "";
        SetSidePanels();
    }

    private void OnToggleMaximise(object sender, RoutedEventArgs e) => SetMaximised(MaximiseItem.IsChecked);
    private void OnToggleMaximiseButton(object sender, RoutedEventArgs e) => SetMaximised(!_maximised);

    // ---------------------------------------------------------------- layout persistence

    private void RestoreLayout()
    {
        if (TryReadDouble("layout.left", out var l)) _leftWidth = LeftColumn.Width = new GridLength(Math.Clamp(l, 250, 700));
        if (TryReadDouble("layout.right", out var r)) _rightWidth = RightColumn.Width = new GridLength(Math.Clamp(r, 260, 700));
        if (TryReadDouble("layout.outliner", out var o) && o is > 0.1 and < 0.9)
        {
            OutlinerRow.Height = new GridLength(o, GridUnitType.Star);
            InspectorRow.Height = new GridLength(1 - o, GridUnitType.Star);
        }
        var c = _settings.Catalogue;
        (c.DefaultView == "grid" ? ViewGrid : ViewList).IsChecked = true;
        SortBox.SelectedIndex = Math.Clamp(c.DefaultSort, 0, SortBox.Items.Count - 1);
        _favorites = new HashSet<string>((ReadSetting("favorites") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
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
        WriteSetting("layout.left", _leftWidth.Value.ToString(CultureInfo.InvariantCulture));
        WriteSetting("layout.right", _rightWidth.Value.ToString(CultureInfo.InvariantCulture));
        double total = OutlinerRow.ActualHeight + InspectorRow.ActualHeight;
        if (total > 0 && OutlinerRow.ActualHeight > 0 && InspectorRow.ActualHeight > 0)
            WriteSetting("layout.outliner", (OutlinerRow.ActualHeight / total).ToString("0.###", CultureInfo.InvariantCulture));
        WriteSetting("workspace", _workspace == Workspace.Catalogue ? "catalogue" : "world");
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => SaveLayout();

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        _leftWidth = new GridLength(DefaultLeft);
        _rightWidth = new GridLength(DefaultRight);
        LeftColumn.Width = _leftWidth;
        RightColumn.Width = _rightWidth;
        OutlinerRow.Height = new GridLength(2, GridUnitType.Star);
        InspectorRow.Height = new GridLength(3, GridUnitType.Star);
        _settings.Appearance.ShowBrowser = _settings.Appearance.ShowHierarchy = _settings.Appearance.ShowInspector = true;
        SetMaximised(false);
        ApplySettings();
        SaveLayout();
        Status("Panel layout reset.");
    }

    private static bool TryReadDouble(string key, out double v) =>
        double.TryParse(ReadSetting(key), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    // ---------------------------------------------------------------- navigation menus

    private void OnOpenNavMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnToolsMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuSelect.IsChecked = _tool is null;
        MenuMove.IsChecked = _tool == Rendering.GizmoMode.Move;
        MenuRotate.IsChecked = _tool == Rendering.GizmoMode.Rotate;
        MenuScale.IsChecked = _tool == Rendering.GizmoMode.Scale;
        MenuBoundary.IsChecked = ToolBoundary.IsChecked == true;
        UpdateUndoMenu();
    }

    private void OnMenuBoundary(object sender, RoutedEventArgs e) => SetBoundaryTool(ToolBoundary.IsChecked != true);

    private void OnDeselect(object sender, RoutedEventArgs e) { Select(null); RebuildHierarchy(); }

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
        rows.Add(("Alt + left drag", "Orbit · middle drag pans · wheel dollies"));
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
                g.Children.Add(new TextBlock { Text = keys, FontFamily = (FontFamily)FindResource("Display"), FontSize = 11.5, Foreground = (Brush)FindResource("AmberHi"), TextWrapping = TextWrapping.Wrap });
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

    /// <summary>When the viewport is too narrow for both overlay bars, the layer bar drops below the tool bar.</summary>
    private void OnWorldViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ToolOverlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        LayerOverlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool fits = ToolOverlay.DesiredSize.Width + LayerOverlay.DesiredSize.Width + 36 <= WorldView.ActualWidth;
        LayerOverlay.Margin = fits ? new Thickness(12) : new Thickness(12, 12 + ToolOverlay.DesiredSize.Height + 6, 12, 12);
        BoundaryBanner.Margin = new Thickness(0, fits ? 58 : 100, 0, 0);
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
            int caption = 0x00111311;                                         // COLORREF 0x00BBGGRR = #111311 (Bg1)
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));        // DWMWA_CAPTION_COLOR (Windows 11)
            int text = 0x00D2E0E6;                                            // #E6E0D2
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));           // DWMWA_TEXT_COLOR
            int border = 0x00222622;
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));         // DWMWA_BORDER_COLOR
        }
        catch (Exception) { /* older Windows: keep the system title bar */ }
    }
}
