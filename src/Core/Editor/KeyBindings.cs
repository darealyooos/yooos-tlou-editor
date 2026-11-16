using System.Text.Json;

namespace FactionsReborn.WorldEditor.Core.Editor;

[Flags]
public enum Mods { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

/// <summary>
/// A key plus modifiers, independent of any UI framework. <see cref="Key"/> uses WPF <c>System.Windows.Input.Key</c>
/// enum names ("D", "Delete", "F11", "OemComma"…), so the desktop app maps it with Enum.Parse.
/// </summary>
public readonly record struct KeyChord(string Key, Mods Mods)
{
    public static readonly KeyChord None = new("", Mods.None);
    public bool IsEmpty => string.IsNullOrEmpty(Key);

    private static readonly Dictionary<string, string> Pretty = new(StringComparer.Ordinal)
    {
        ["OemComma"] = ",", ["OemPeriod"] = ".", ["OemPlus"] = "=", ["OemMinus"] = "-", ["Oem2"] = "/", ["OemQuestion"] = "/",
        ["Oem1"] = ";", ["OemSemicolon"] = ";", ["Oem3"] = "`", ["OemTilde"] = "`", ["Oem4"] = "[", ["OemOpenBrackets"] = "[",
        ["Oem6"] = "]", ["OemCloseBrackets"] = "]", ["Oem5"] = "\\", ["OemPipe"] = "\\", ["Oem7"] = "'", ["OemQuotes"] = "'",
        ["Next"] = "PageDown", ["PageDown"] = "PageDown", ["Prior"] = "PageUp", ["PageUp"] = "PageUp", ["Back"] = "Backspace",
        ["Return"] = "Enter", ["Enter"] = "Enter", ["Space"] = "Space", ["Delete"] = "Delete", ["Insert"] = "Insert",
    };

    /// <summary>Display text, e.g. "Ctrl + Shift + S".</summary>
    public string Display
    {
        get
        {
            if (IsEmpty) return "Unassigned";
            var parts = new List<string>(4);
            if (Mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
            if (Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
            if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
            parts.Add(KeyLabel(Key));
            return string.Join(" + ", parts);
        }
    }

    public static string KeyLabel(string key)
    {
        if (Pretty.TryGetValue(key, out var p)) return p;
        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) return key[1..];
        if (key.StartsWith("NumPad", StringComparison.Ordinal)) return "Num " + key[6..];
        return key;
    }

    /// <summary>Storage text, e.g. "Ctrl+Shift+S" (key part is the WPF enum name).</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        var s = "";
        if (Mods.HasFlag(Mods.Ctrl)) s += "Ctrl+";
        if (Mods.HasFlag(Mods.Shift)) s += "Shift+";
        if (Mods.HasFlag(Mods.Alt)) s += "Alt+";
        return s + Key;
    }

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (text is null) return false;
        text = text.Trim();
        if (text.Length == 0) return true; // explicitly unassigned
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var mods = Mods.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Mods.Ctrl; break;
                case "shift": mods |= Mods.Shift; break;
                case "alt": mods |= Mods.Alt; break;
                default: return false;
            }
        }
        var key = parts[^1];
        if (key.Length == 0 || key.Any(c => !char.IsLetterOrDigit(c))) return false;
        chord = new KeyChord(key, mods);
        return true;
    }
}

/// <summary>Where a binding is active. Fly bindings are only read while the camera is in mouse-look.</summary>
public enum ShortcutContext { Editor, Fly }

public sealed record ShortcutAction(string Id, string Name, string Category, KeyChord Default, ShortcutContext Context = ShortcutContext.Editor, string? Description = null);

/// <summary>Every rebindable editor action and its default chord.</summary>
public static class ShortcutCatalog
{
    private static KeyChord K(string key, Mods m = Mods.None) => new(key, m);

    public static readonly IReadOnlyList<ShortcutAction> All =
    [
        new("tool.select", "Select tool", "Tools", K("Q"), Description: "Click to select; no transform gizmo"),
        new("tool.move", "Move tool", "Tools", K("W")),
        new("tool.rotate", "Rotate tool", "Tools", K("E")),
        new("tool.scale", "Scale tool", "Tools", K("R")),
        new("tool.boundary", "Draw arena boundary", "Tools", K("B")),

        new("edit.undo", "Undo", "Edit", K("Z", Mods.Ctrl)),
        new("edit.redo", "Redo", "Edit", K("Y", Mods.Ctrl)),
        new("edit.duplicate", "Duplicate selection", "Edit", K("D", Mods.Ctrl)),
        new("edit.delete", "Delete selection", "Edit", K("Delete")),
        new("edit.spawnWlf", "Add WLF spawn", "Edit", KeyChord.None),
        new("edit.spawnScars", "Add Seraphite spawn", "Edit", KeyChord.None),

        new("file.new", "New scene", "File", K("N", Mods.Ctrl)),
        new("file.open", "Open scene", "File", K("O", Mods.Ctrl)),
        new("file.save", "Save scene", "File", K("S", Mods.Ctrl)),
        new("file.saveAs", "Save scene as", "File", K("S", Mods.Ctrl | Mods.Shift)),

        new("view.focus", "Focus selected object", "Camera", K("F")),
        new("view.frameAll", "Frame everything", "Camera", K("F", Mods.Shift)),
        new("view.resetCamera", "Reset camera", "Camera", K("Home")),
        new("view.grid", "Toggle grid", "View", K("G")),
        new("view.browser", "Toggle Asset Browser", "View", K("A", Mods.Shift)),
        new("view.inspector", "Toggle Inspector", "View", K("I", Mods.Shift)),
        new("view.hierarchy", "Toggle Scene Hierarchy", "View", K("H", Mods.Shift)),
        new("view.maximise", "Maximise viewport", "View", K("F11")),
        new("view.shortcuts", "Show shortcut sheet", "View", K("F1")),

        new("nav.catalogue", "Asset Catalogue workspace", "Navigation", K("D1", Mods.Ctrl)),
        new("nav.world", "World Editor workspace", "Navigation", K("D2", Mods.Ctrl)),
        new("nav.settings", "Settings", "Navigation", K("OemComma", Mods.Ctrl)),
        new("browser.search", "Search assets", "Navigation", K("F", Mods.Ctrl)),

        new("fly.forward", "Fly forward", "Camera fly (hold right mouse)", K("W"), ShortcutContext.Fly),
        new("fly.back", "Fly backward", "Camera fly (hold right mouse)", K("S"), ShortcutContext.Fly),
        new("fly.left", "Fly left", "Camera fly (hold right mouse)", K("A"), ShortcutContext.Fly),
        new("fly.right", "Fly right", "Camera fly (hold right mouse)", K("D"), ShortcutContext.Fly),
        new("fly.up", "Fly up", "Camera fly (hold right mouse)", K("E"), ShortcutContext.Fly),
        new("fly.down", "Fly down", "Camera fly (hold right mouse)", K("Q"), ShortcutContext.Fly),
    ];

    public static readonly IReadOnlyDictionary<string, ShortcutAction> ById = All.ToDictionary(a => a.Id);
}

/// <summary>User key bindings: defaults plus overrides, conflict detection, reserved keys and profile files.</summary>
public sealed class KeyBindings
{
    public const int ProfileVersion = 1;
    private readonly Dictionary<string, KeyChord> _overrides = new(StringComparer.Ordinal);

    public event Action? Changed;

    public KeyChord Get(string id) =>
        _overrides.TryGetValue(id, out var c) ? c : ShortcutCatalog.ById.TryGetValue(id, out var a) ? a.Default : KeyChord.None;

    public bool IsDefault(string id) => !_overrides.ContainsKey(id) || _overrides[id] == ShortcutCatalog.ById[id].Default;

    public void Set(string id, KeyChord chord)
    {
        if (!ShortcutCatalog.ById.ContainsKey(id)) throw new ArgumentException($"Unknown action {id}");
        if (chord == ShortcutCatalog.ById[id].Default) _overrides.Remove(id); else _overrides[id] = chord;
        Changed?.Invoke();
    }

    public void Reset(string id) { if (_overrides.Remove(id)) Changed?.Invoke(); }
    public void ResetAll() { if (_overrides.Count == 0) return; _overrides.Clear(); Changed?.Invoke(); }

    /// <summary>Actions in the same context that already use <paramref name="chord"/> (excluding <paramref name="id"/>).</summary>
    public IReadOnlyList<ShortcutAction> ConflictsWith(string id, KeyChord chord)
    {
        if (chord.IsEmpty) return [];
        var ctx = ShortcutCatalog.ById[id].Context;
        return ShortcutCatalog.All.Where(a => a.Id != id && a.Context == ctx && Get(a.Id) == chord).ToList();
    }

    /// <summary>Every group of two or more actions sharing one chord within a context.</summary>
    public IReadOnlyList<(KeyChord Chord, IReadOnlyList<ShortcutAction> Actions)> AllConflicts() =>
        ShortcutCatalog.All.Where(a => !Get(a.Id).IsEmpty)
            .GroupBy(a => (a.Context, Get(a.Id)))
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key.Item2, (IReadOnlyList<ShortcutAction>)g.ToList()))
            .ToList();

    /// <summary>The action bound to <paramref name="chord"/> in a context (first match when conflicting).</summary>
    public ShortcutAction? Resolve(KeyChord chord, ShortcutContext ctx = ShortcutContext.Editor) =>
        ShortcutCatalog.All.FirstOrDefault(a => a.Context == ctx && Get(a.Id) == chord);

    private static readonly HashSet<string> ModifierKeys = new(StringComparer.Ordinal)
    {
        "LeftCtrl", "RightCtrl", "LeftShift", "RightShift", "LeftAlt", "RightAlt", "System", "LWin", "RWin", "Apps",
        "ImeProcessed", "DeadCharProcessed", "None", "Capital", "NumLock", "Scroll",
    };

    /// <summary>Keys that cannot be assigned: modifiers alone, keys owned by Windows or by the editor's fixed behaviour.</summary>
    public static bool IsReserved(KeyChord chord, ShortcutContext ctx, out string reason)
    {
        reason = "";
        if (chord.IsEmpty) return false;
        if (ModifierKeys.Contains(chord.Key)) { reason = "A modifier on its own cannot be a shortcut."; return true; }
        if (chord.Key == "Escape") { reason = "Escape cancels rebinding and deselects; it cannot be reassigned."; return true; }
        if (chord.Key == "Tab") { reason = "Tab moves keyboard focus between controls."; return true; }
        if (chord.Key == "F4" && chord.Mods.HasFlag(Mods.Alt)) { reason = "Alt + F4 closes the window."; return true; }
        if (chord.Key == "Space" && chord.Mods == Mods.Alt) { reason = "Alt + Space opens the window menu."; return true; }
        if (chord.Key == "F10" && chord.Mods == Mods.None) { reason = "F10 is reserved by Windows for menu activation."; return true; }
        if (chord.Key == "Delete" && chord.Mods == (Mods.Ctrl | Mods.Alt)) { reason = "Ctrl + Alt + Delete belongs to Windows."; return true; }
        if (chord.Key is "Snapshot" or "PrintScreen") { reason = "Print Screen belongs to Windows."; return true; }
        if (ctx == ShortcutContext.Fly && chord.Mods != Mods.None) { reason = "Fly keys are held while looking around; use a key without modifiers."; return true; }
        return false;
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>Overrides only (action id → chord text). Unassigned is stored as "".</summary>
    public Dictionary<string, string> ToDictionary() => _overrides.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

    /// <summary>Loads overrides; unknown ids and unparsable chords are skipped and reported.</summary>
    public List<string> Load(IReadOnlyDictionary<string, string>? stored)
    {
        var problems = new List<string>();
        _overrides.Clear();
        if (stored is not null)
            foreach (var (id, text) in stored)
            {
                if (!ShortcutCatalog.ById.TryGetValue(id, out var action)) { problems.Add($"unknown action '{id}'"); continue; }
                if (!KeyChord.TryParse(text, out var chord)) { problems.Add($"'{text}' for {action.Name}"); continue; }
                if (IsReserved(chord, action.Context, out _)) { problems.Add($"reserved '{text}' for {action.Name}"); continue; }
                if (chord != action.Default) _overrides[id] = chord;
            }
        Changed?.Invoke();
        return problems;
    }

    private sealed class Profile
    {
        public string Format { get; set; } = "yooos-keybindings";
        public int Version { get; set; } = ProfileVersion;
        public Dictionary<string, string> Bindings { get; set; } = [];
    }

    /// <summary>Exports every binding (not just overrides) so the profile is readable on its own.</summary>
    public string ExportProfile() => JsonSerializer.Serialize(new Profile
    {
        Bindings = ShortcutCatalog.All.ToDictionary(a => a.Id, a => Get(a.Id).ToString()),
    }, new JsonSerializerOptions { WriteIndented = true });

    public List<string> ImportProfile(string json)
    {
        var p = JsonSerializer.Deserialize<Profile>(json) ?? throw new InvalidDataException("Empty profile.");
        if (p.Format != "yooos-keybindings") throw new InvalidDataException("Not a Yooos keybinding profile.");
        if (p.Version > ProfileVersion) throw new InvalidDataException($"Profile version {p.Version} is newer than this editor ({ProfileVersion}).");
        return Load(p.Bindings);
    }
}
