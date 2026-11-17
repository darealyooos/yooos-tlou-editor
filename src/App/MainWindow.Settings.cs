using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Editor;
using Microsoft.Win32;

namespace FactionsReborn.WorldEditor;

public enum PanelKind { Browser, Hierarchy, Inspector }

/// <summary>Settings workspace: persistence (versioned JSON in local app data), live application, and the pages.</summary>
public partial class MainWindow
{
    private static readonly string SettingsDir = EditorSettings.DefaultDirectory;
    private static string SettingsPath => Path.Combine(SettingsDir, EditorSettings.FileName);
    private static EditorSettings? _settingsStatic;
    private EditorSettings _settings = null!;
    private DispatcherTimer? _saveTimer, _autosaveTimer;
    private string _settingsSection = "General";
    private static readonly string[] SettingsSectionNames = ["General", "Appearance", "Controls", "Viewport", "Asset Catalogue", "Performance"];

    // ---------------------------------------------------------------- load / save

    private void LoadSettings()
    {
        _settings = EditorSettings.Load(SettingsPath, EditorSettings.LegacyFile, out var note);
        _settingsStatic = _settings;
        var problems = _bindings.Load(_settings.Keybindings);
        if (note is not null) Status(note);
        if (problems.Count > 0) Status($"Ignored {problems.Count} invalid key binding(s): {string.Join("; ", problems.Take(3))}");
        _bindings.Changed += () => { _settings.Keybindings = _bindings.ToDictionary(); SaveSettingsSoon(); };
    }

    /// <summary>Coalesces bursts of changes (sliders) into one write.</summary>
    private void SaveSettingsSoon()
    {
        if (ProfilePath is not null || SelfTestPath is not null || SnapshotPath is not null) return; // automated runs never touch preferences
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); SaveSettingsNow(); }, Dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettingsNow()
    {
        if (ProfilePath is not null || SelfTestPath is not null || SnapshotPath is not null) return;
        try { _settings.Save(SettingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status($"Could not save settings: {ex.Message}"); }
    }

    // UI state (panel sizes, favourites, game folder…) lives in Settings.State.
    private static string? ReadSetting(string key) => _settingsStatic?.State.GetValueOrDefault(key);

    private void WriteSetting(string key, string value)
    {
        if (_settings.State.GetValueOrDefault(key) == value) return;
        _settings.State[key] = value;
        SaveSettingsSoon();
    }

    // ---------------------------------------------------------------- apply

    private static readonly Dictionary<string, (double Tile, double Image)> TileSizes = new()
    {
        ["small"] = (84, 64), ["medium"] = (104, 86), ["large"] = (140, 118),
    };

    private double TileWidth => TileSizes[_settings.Catalogue.ThumbnailSize].Tile;

    /// <summary>Applies every setting that has a live effect. Safe to call repeatedly.</summary>
    private void ApplySettings()
    {
        var a = _settings.Appearance;
        RootDock.LayoutTransform = Math.Abs(a.UiScale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(a.UiScale, a.UiScale);
        bool compact = a.Density == "compact";
        Application.Current.Resources["RowHeight"] = compact ? 26.0 : 34.0;
        Application.Current.Resources["TileImageHeight"] = TileSizes[_settings.Catalogue.ThumbnailSize].Image * (compact ? 0.85 : 1.0);
        MenuToggleBrowser.IsChecked = a.ShowBrowser;
        MenuToggleHierarchy.IsChecked = a.ShowHierarchy;
        MenuToggleInspector.IsChecked = a.ShowInspector;
        SetSidePanels();

        _camera.Perspective.FieldOfView = _settings.Viewport.FieldOfView;
        if (ShowGrid.IsChecked != _settings.Viewport.ShowGrid) { ShowGrid.IsChecked = _settings.Viewport.ShowGrid; ApplyLayerVisibility(); }
        OnAssetGridSizeChanged(this, null!);
        RefreshGizmo();

        _autosaveTimer?.Stop();
        if (_settings.General.AutosaveMinutes > 0 && SelfTestPath is null && ProfilePath is null && SnapshotPath is null)
        {
            _autosaveTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _autosaveTimer.Interval = TimeSpan.FromMinutes(_settings.General.AutosaveMinutes);
            _autosaveTimer.Tick -= OnAutosaveTick;
            _autosaveTimer.Tick += OnAutosaveTick;
            _autosaveTimer.Start();
        }
    }

    private void Changed(Action apply) { apply(); ApplySettings(); SaveSettingsSoon(); }

    private void TogglePanel(PanelKind kind)
    {
        var a = _settings.Appearance;
        if (_workspace != Workspace.World && kind != PanelKind.Browser) SetWorkspace(Workspace.World);
        if (_maximised) SetMaximised(false);
        switch (kind)
        {
            case PanelKind.Browser: a.ShowBrowser = !a.ShowBrowser; break;
            case PanelKind.Hierarchy: a.ShowHierarchy = !a.ShowHierarchy; break;
            case PanelKind.Inspector: a.ShowInspector = !a.ShowInspector; break;
        }
        ApplySettings();
        SaveSettingsSoon();
    }

    private void OnPanelMenu(object sender, RoutedEventArgs e)
    {
        var a = _settings.Appearance;
        a.ShowBrowser = MenuToggleBrowser.IsChecked;
        a.ShowHierarchy = MenuToggleHierarchy.IsChecked;
        a.ShowInspector = MenuToggleInspector.IsChecked;
        ApplySettings();
        SaveSettingsSoon();
    }

    // ---------------------------------------------------------------- autosave

    private string AutosaveDir => Path.Combine(ScenesDir(), "Autosave");

    private void OnAutosaveTick(object? sender, EventArgs e)
    {
        if (!_dirty || (_doc.Objects.Count == 0 && _doc.Spawns.Count == 0 && _doc.Boundary.Points.Count == 0 && _doc.Maps.Count == 0)) return;
        try
        {
            Directory.CreateDirectory(AutosaveDir);
            var name = string.Concat(SceneDisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(AutosaveDir, name + ".autosave.frscene");
            _doc.Save(path); // a recovery copy: the user's own file and the dirty flag are left alone
            Status($"Autosaved a recovery copy to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status($"Autosave failed: {ex.Message}"); }
    }

    private void OnOpenAutosaveFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AutosaveDir);
        Process.Start(new ProcessStartInfo { FileName = AutosaveDir, UseShellExecute = true });
    }

    // ---------------------------------------------------------------- settings workspace

    private void OnCustomiseShortcuts(object sender, RoutedEventArgs e) { _settingsSection = "Controls"; SetWorkspace(Workspace.Settings); }

    private void InitSettingsView()
    {
        SettingsSections.ItemsSource = SettingsSectionNames;
        SettingsSections.SelectedItem = _settingsSection;
        SettingsFileText.Text = $"Saved automatically to\n{SettingsPath}";
        SettingsFileText.ToolTip = SettingsPath;
    }

    private void OnSettingsSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsSections.SelectedItem is string s) { _settingsSection = s; BuildSettingsPage(); }
    }

    private void BuildSettingsPage()
    {
        CancelCapture();
        SettingsContent.Children.Clear();
        SettingsScroll.ScrollToTop();
        if (SettingsSections.SelectedItem as string != _settingsSection) { SettingsSections.SelectedItem = _settingsSection; return; }
        switch (_settingsSection)
        {
            case "General": BuildGeneralPage(); break;
            case "Appearance": BuildAppearancePage(); break;
            case "Controls": BuildControlsPage(); break;
            case "Viewport": BuildViewportPage(); break;
            case "Asset Catalogue": BuildCataloguePage(); break;
            case "Performance": BuildPerformancePage(); break;
        }
    }

    private void BuildGeneralPage()
    {
        var g = _settings.General;
        PageHeader("General", "Startup, saving and confirmations.");
        SettingsGroup("Startup");
        ChoiceRow("When the editor starts", null, [("empty", "Empty scene"), ("lastScene", "Reopen last scene")], g.Startup, v => Changed(() => g.Startup = v));
        SettingsGroup("Saving");
        SliderRow("Autosave recovery copy", "Every N minutes while there are unsaved changes, to Scenes\\Autosave. Your own file is never overwritten. 0 = off.",
            0, 30, 1, v => v == 0 ? "Off" : $"{v:0} min", g.AutosaveMinutes, v => Changed(() => g.AutosaveMinutes = (int)v));
        ButtonRow("Scenes folder", g.ScenesFolder ?? ScenesDir(), "Change…", () =>
        {
            var dlg = new OpenFolderDialog { Title = "Folder for .frscene files", InitialDirectory = ScenesDir() };
            if (dlg.ShowDialog(this) == true) { Changed(() => g.ScenesFolder = dlg.FolderName); BuildSettingsPage(); }
        });
        SettingsGroup("Confirmations");
        ToggleRow("Ask before discarding unsaved changes", "When creating, opening or closing a scene.", g.ConfirmDiscard, v => Changed(() => g.ConfirmDiscard = v));
        ToggleRow("Ask before deleting objects", "Deletion can always be undone.", g.ConfirmDelete, v => Changed(() => g.ConfirmDelete = v));
        SettingsGroup("Language");
        InfoRow("Interface language", "English (the only language available in this version)");
        SettingsGroup("Game files");
        ButtonRow("TLOU Part II install", _buildDir ?? "Not found", "Change…", OnChangeGameFolder);
    }

    private void BuildAppearancePage()
    {
        var a = _settings.Appearance;
        PageHeader("Appearance", "Scale, spacing and panels.");
        SettingsGroup("Interface");
        SliderRow("UI scale", null, 0.8, 1.6, 0.05, v => $"{v * 100:0}%", a.UiScale, v => Changed(() => a.UiScale = Math.Round(v, 2)));
        ChoiceRow("Spacing", "Compact fits more rows in the asset list.", [("comfortable", "Comfortable"), ("compact", "Compact")], a.Density, v => Changed(() => a.Density = v));
        InfoRow("Theme", "Dusk (the only theme in this version)");
        SettingsGroup("Panels");
        ToggleRow("Asset Browser", null, a.ShowBrowser, v => Changed(() => a.ShowBrowser = v));
        ToggleRow("Scene Hierarchy", null, a.ShowHierarchy, v => Changed(() => a.ShowHierarchy = v));
        ToggleRow("Inspector", null, a.ShowInspector, v => Changed(() => a.ShowInspector = v));
        ButtonRow("Panel sizes", "Restore the default widths and split.", "Reset layout", () => OnResetLayout(this, new RoutedEventArgs()));
    }

    private void BuildViewportPage()
    {
        var v = _settings.Viewport;
        PageHeader("Viewport", "Camera navigation and defaults.");
        SettingsGroup("Camera");
        SliderRow("Fly speed", "Right mouse + fly keys.", 0.5, 40, 0.5, x => $"{x:0.#} m/s", v.FlySpeed, x => Changed(() => v.FlySpeed = x));
        SliderRow("Fast multiplier", "While Shift is held.", 1, 20, 0.5, x => $"×{x:0.#}", v.FastMultiplier, x => Changed(() => v.FastMultiplier = x));
        SliderRow("Look sensitivity", null, 0.1, 4, 0.05, x => $"{x:0.00}", v.LookSensitivity, x => Changed(() => v.LookSensitivity = x));
        ToggleRow("Invert vertical look", null, v.InvertY, x => Changed(() => v.InvertY = x));
        SliderRow("Zoom speed", "Mouse wheel dolly.", 0.1, 5, 0.1, x => $"×{x:0.0}", v.ZoomSpeed, x => Changed(() => v.ZoomSpeed = x));
        ToggleRow("Smooth zoom", "Eases the wheel dolly instead of jumping.", v.SmoothZoom, x => Changed(() => v.SmoothZoom = x));
        ToggleRow("Alt + left mouse orbits", "Around the selection, or the point under the cursor.", v.AltOrbit, x => Changed(() => v.AltOrbit = x));
        SliderRow("Field of view", "Horizontal.", 40, 110, 1, x => $"{x:0}°", v.FieldOfView, x => Changed(() => v.FieldOfView = x));
        SettingsGroup("Defaults");
        ToggleRow("Ground grid", null, v.ShowGrid, x => Changed(() => v.ShowGrid = x));
        ChoiceRow("Tool at startup", null, [("move", "Move"), ("select", "Select")], v.DefaultTool, x => Changed(() => v.DefaultTool = x));
    }

    private void BuildCataloguePage()
    {
        var c = _settings.Catalogue;
        PageHeader("Asset Catalogue", "Browser layout and previews.");
        SettingsGroup("Browser");
        ChoiceRow("View mode", null, [("list", "List"), ("grid", "Grid")], c.DefaultView, x =>
        {
            Changed(() => c.DefaultView = x);
            (x == "grid" ? ViewGrid : ViewList).IsChecked = true;
        });
        ChoiceRow("Thumbnail size", "Grid tiles.", [("small", "Small"), ("medium", "Medium"), ("large", "Large")], c.ThumbnailSize, x => Changed(() => c.ThumbnailSize = x));
        ChoiceRow("Sorting", null, [("0", "Name A–Z"), ("1", "Name Z–A"), ("2", "Largest"), ("3", "Smallest"), ("4", "Archive")], c.DefaultSort.ToString(), x =>
        {
            Changed(() => c.DefaultSort = int.Parse(x));
            SortBox.SelectedIndex = c.DefaultSort;
        });
        SettingsGroup("Preview");
        ToggleRow("Preview the selected asset", "Decodes the mesh when an asset is selected. Off: only metadata is shown until you place it.", c.AutoPreview, x => Changed(() => c.AutoPreview = x));
        InfoRow("Favourites", $"{_favorites.Count} asset(s)");
    }

    private void BuildPerformancePage()
    {
        var p = _settings.Performance;
        PageHeader("Performance", "Background work, caches and memory.");
        SettingsGroup("Thumbnails");
        ToggleRow("Render thumbnails for visible assets", "Decodes each visible prop once in the background.", p.AutoThumbnails, x => Changed(() =>
        {
            p.AutoThumbnails = x;
            if (!x) _thumbQueue.Clear(); else foreach (var i in _shownItems.Take(60)) EnsureThumbnail(i);
        }));
        ToggleRow("Keep thumbnails on disk", $"Cache folder: {ThumbDir}", p.ThumbnailDiskCache, x => Changed(() => p.ThumbnailDiskCache = x));
        ButtonRow("Thumbnail cache", $"{ThumbCacheSummary()}", "Clear cache", () => { OnClearThumbCache(this, new RoutedEventArgs()); BuildSettingsPage(); });
        SettingsGroup("Previews");
        ToggleRow("Textured previews and thumbnails", "Off builds untextured meshes: faster, uses less memory.", p.TexturedPreviews, x => Changed(() => p.TexturedPreviews = x));
        SliderRow("Preview meshes kept in memory", "Recently previewed assets reopen instantly; older ones are released.", 2, 64, 1, x => $"{x:0}", p.PreviewCacheSize, x => Changed(() => { p.PreviewCacheSize = (int)x; TrimPreviewCache(); }));
        SettingsGroup("Memory");
        ToggleRow("Release paks not used by the scene", "After deleting objects, closing or opening scenes, decoded paks no longer referenced are freed.", p.TrimUnusedPaks, x => Changed(() => p.TrimUnusedPaks = x));
        ButtonRow("Memory in use", MemorySummary(), "Release now", () => { TrimMemory(force: true); BuildSettingsPage(); });
    }

    private static string MemorySummary()
    {
        using var proc = Process.GetCurrentProcess();
        return $"{GC.GetTotalMemory(false) / 1048576:N0} MB managed · {proc.WorkingSet64 / 1048576:N0} MB working set";
    }

    private static string ThumbCacheSummary()
    {
        try
        {
            if (!Directory.Exists(ThumbDir)) return "Empty";
            var files = new DirectoryInfo(ThumbDir).GetFiles("*.png");
            return $"{files.Length:N0} images · {files.Sum(f => f.Length) / 1048576.0:0.0} MB";
        }
        catch (Exception) { return "Unavailable"; }
    }

    private void OnChangeGameFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Select The Last of Us Part II install folder (or its build\\pc\\main)" };
        if (dlg.ShowDialog(this) != true) return;
        var dir = GameLocator.FindBuildDir(dlg.FolderName);
        if (dir is null) { Status($"No common.psarc under {dlg.FolderName}: not a TLOU Part II install."); return; }
        WriteSetting("buildDir", dir);
        Status($"Game folder set to {dir}. Restart the editor to re-index.");
        BuildSettingsPage();
    }

    // ---------------------------------------------------------------- page widgets

    private void PageHeader(string title, string subtitle)
    {
        SettingsContent.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Title"), FontSize = 30 });
        SettingsContent.Children.Add(new TextBlock { Text = subtitle, Foreground = Res("Text3"), FontSize = 12.5, Margin = new Thickness(0, 4, 0, 6) });
    }

    private void SettingsGroup(string title) => AddHeading(SettingsContent, title);

    private Grid SettingRow(string label, string? hint, UIElement control, double controlWidth = 300)
    {
        var g = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(controlWidth) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Res("Text") });
        if (hint is not null) text.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 3, 0, 0) });
        g.Children.Add(text);
        Grid.SetColumn(control, 1);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(control);
        SettingsContent.Children.Add(g);
        return g;
    }

    private void ToggleRow(string label, string? hint, bool value, Action<bool> set)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value, HorizontalAlignment = HorizontalAlignment.Right };
        box.Click += (_, _) => set(box.IsChecked == true);
        SettingRow(label, hint, box);
    }

    private void SliderRow(string label, string? hint, double min, double max, double step, Func<double, string> format, double value, Action<double> set)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, SmallChange = step, LargeChange = step * 5, VerticalAlignment = VerticalAlignment.Center };
        var readout = new TextBlock { Text = format(value), FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11.5, Foreground = Res("Text2"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { readout.Text = format(e.NewValue); set(e.NewValue); };
        Grid.SetColumn(readout, 1);
        grid.Children.Add(slider);
        grid.Children.Add(readout);
        SettingRow(label, hint, grid);
    }

    private void ChoiceRow(string label, string? hint, (string Value, string Label)[] options, string value, Action<string> set)
    {
        var border = new Border { BorderBrush = Res("Line2"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new UniformGrid { Rows = 1 };
        string group = "choice-" + label;
        foreach (var (v, l) in options)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = group, Content = l, IsChecked = v == value, Padding = new Thickness(12, 6, 12, 6) };
            rb.Checked += (_, _) => set(v);
            grid.Children.Add(rb);
        }
        border.Child = grid;
        SettingRow(label, hint, border);
    }

    private void ButtonRow(string label, string? hint, string button, Action click)
    {
        var b = new Button { Content = button, HorizontalAlignment = HorizontalAlignment.Right };
        b.Click += (_, _) => click();
        SettingRow(label, hint, b);
    }

    private void InfoRow(string label, string value) =>
        SettingRow(label, null, new TextBlock { Text = value, Foreground = Res("Text3"), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right });
}
