using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactionsReborn.WorldEditor.Core.Editor;

public sealed class GeneralSettings
{
    /// <summary>"empty" or "lastScene".</summary>
    public string Startup { get; set; } = "empty";
    /// <summary>0 = off. Writes a recovery copy next to the scenes (never over the user's file).</summary>
    public int AutosaveMinutes { get; set; } = 5;
    public bool ConfirmDelete { get; set; }
    public bool ConfirmDiscard { get; set; } = true;
    public string Language { get; set; } = "en";
    public string? ScenesFolder { get; set; }
}

public sealed class AppearanceSettings
{
    public double UiScale { get; set; } = 1.0;
    /// <summary>"comfortable" or "compact".</summary>
    public string Density { get; set; } = "comfortable";
    public string Theme { get; set; } = "dusk";
    public bool ShowBrowser { get; set; } = true;
    public bool ShowHierarchy { get; set; } = true;
    public bool ShowInspector { get; set; } = true;
}

public sealed class ViewportSettings
{
    public bool ShowGrid { get; set; } = true;
    public double FlySpeed { get; set; } = 8;
    public double FastMultiplier { get; set; } = 5;
    public double LookSensitivity { get; set; } = 1.0;
    public bool InvertY { get; set; }
    public double ZoomSpeed { get; set; } = 1.0;
    public bool SmoothZoom { get; set; } = true;
    public bool AltOrbit { get; set; } = true;
    public double FieldOfView { get; set; } = 70;
    /// <summary>"select" or "move".</summary>
    public string DefaultTool { get; set; } = "move";
}

public sealed class CatalogueSettings
{
    /// <summary>"list" or "grid".</summary>
    public string DefaultView { get; set; } = "list";
    /// <summary>"small", "medium" or "large".</summary>
    public string ThumbnailSize { get; set; } = "medium";
    public int DefaultSort { get; set; }
    public bool AutoPreview { get; set; } = true;
}

public sealed class PerformanceSettings
{
    public bool AutoThumbnails { get; set; } = true;
    public bool ThumbnailDiskCache { get; set; } = true;
    public bool TexturedPreviews { get; set; } = true;
    public int PreviewCacheSize { get; set; } = 16;
    public bool TrimUnusedPaks { get; set; } = true;
}

/// <summary>
/// All persisted preferences, one versioned JSON file in the user's local app data (never the game folder).
/// <see cref="State"/> holds UI state such as panel sizes, favourites and the game folder.
/// </summary>
public sealed class EditorSettings
{
    public const int CurrentVersion = 1;
    public const string FileName = "settings.json";

    public int Version { get; set; } = CurrentVersion;
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public ViewportSettings Viewport { get; set; } = new();
    public CatalogueSettings Catalogue { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public Dictionary<string, string> Keybindings { get; set; } = [];
    public Dictionary<string, string> State { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YooosTLOUEditor");

    /// <summary>Earlier builds kept a flat string dictionary here.</summary>
    public static string LegacyFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactionsReborn", "WorldEditor", "settings.json");

    /// <summary>
    /// Loads <paramref name="path"/>; if it does not exist, migrates <paramref name="legacyPath"/>; if unreadable,
    /// keeps a .bad copy and starts from defaults. Never throws.
    /// </summary>
    public static EditorSettings Load(string path, string? legacyPath, out string? note)
    {
        note = null;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(path), Json) ?? new EditorSettings();
                if (s.Version > CurrentVersion) note = $"Settings were written by a newer version ({s.Version}); unknown values are ignored.";
                s.Normalise();
                return s;
            }
            if (legacyPath is not null && File.Exists(legacyPath))
            {
                var s = MigrateLegacy(File.ReadAllText(legacyPath));
                note = "Imported preferences from the previous World Editor.";
                return s;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception) { /* best effort */ }
            note = $"Settings file was unreadable ({ex.Message}); defaults restored, old file kept as settings.json.bad.";
        }
        return new EditorSettings();
    }

    public static EditorSettings MigrateLegacy(string legacyJson)
    {
        var s = new EditorSettings();
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(legacyJson) ?? [];
        static bool Num(Dictionary<string, string> d, string k, out double v) =>
            double.TryParse(d.GetValueOrDefault(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        if (Num(d, "camera.speed", out var speed)) s.Viewport.FlySpeed = speed;
        if (Num(d, "camera.fov", out var fov)) s.Viewport.FieldOfView = fov;
        if (d.TryGetValue("thumbs.auto", out var thumbs)) s.Performance.AutoThumbnails = thumbs != "0";
        if (d.TryGetValue("browser.view", out var view)) s.Catalogue.DefaultView = view == "grid" ? "grid" : "list";
        if (d.TryGetValue("browser.sort", out var sort) && int.TryParse(sort, out var si)) s.Catalogue.DefaultSort = si;
        foreach (var key in new[] { "buildDir", "favorites", "layout.left", "layout.right", "layout.outliner", "workspace" })
            if (d.TryGetValue(key, out var v)) s.State[key] = v;
        s.Normalise();
        return s;
    }

    /// <summary>Clamps every value into its supported range (hand-edited files stay safe).</summary>
    public void Normalise()
    {
        General ??= new(); Appearance ??= new(); Viewport ??= new(); Catalogue ??= new(); Performance ??= new();
        Keybindings ??= []; State ??= [];
        General.AutosaveMinutes = Math.Clamp(General.AutosaveMinutes, 0, 120);
        if (General.Startup is not ("empty" or "lastScene")) General.Startup = "empty";
        if (General.Language != "en") General.Language = "en";
        Appearance.UiScale = Math.Clamp(Appearance.UiScale, 0.8, 1.6);
        if (Appearance.Density is not ("comfortable" or "compact")) Appearance.Density = "comfortable";
        if (Appearance.Theme != "dusk") Appearance.Theme = "dusk";
        Viewport.FlySpeed = Math.Clamp(Viewport.FlySpeed, 0.5, 60);
        Viewport.FastMultiplier = Math.Clamp(Viewport.FastMultiplier, 1, 20);
        Viewport.LookSensitivity = Math.Clamp(Viewport.LookSensitivity, 0.1, 4);
        Viewport.ZoomSpeed = Math.Clamp(Viewport.ZoomSpeed, 0.1, 5);
        Viewport.FieldOfView = Math.Clamp(Viewport.FieldOfView, 40, 110);
        if (Viewport.DefaultTool is not ("select" or "move")) Viewport.DefaultTool = "move";
        if (Catalogue.DefaultView is not ("list" or "grid")) Catalogue.DefaultView = "list";
        if (Catalogue.ThumbnailSize is not ("small" or "medium" or "large")) Catalogue.ThumbnailSize = "medium";
        Catalogue.DefaultSort = Math.Clamp(Catalogue.DefaultSort, 0, 4);
        Performance.PreviewCacheSize = Math.Clamp(Performance.PreviewCacheSize, 2, 128);
        Version = CurrentVersion;
    }

    /// <summary>Atomic write: temp file then replace, so a crash never leaves a half-written file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
