using FactionsReborn.WorldEditor.Core.Editor;

namespace WorldEditor.Tests;

public class KeyBindingTests
{
    [Fact]
    public void Defaults_match_the_documented_table_and_have_no_conflicts()
    {
        var b = new KeyBindings();
        Assert.Equal("Q", b.Get("tool.select").ToString());
        Assert.Equal("W", b.Get("tool.move").ToString());
        Assert.Equal("Ctrl+Z", b.Get("edit.undo").ToString());
        Assert.Equal("Ctrl+Y", b.Get("edit.redo").ToString());
        Assert.Equal("Shift+A", b.Get("view.browser").ToString());
        Assert.Equal("Home", b.Get("view.resetCamera").ToString());
        Assert.Empty(b.AllConflicts());
    }

    [Theory]
    [InlineData("Ctrl+Shift+S", "S", Mods.Ctrl | Mods.Shift)]
    [InlineData("ctrl+alt+D1", "D1", Mods.Ctrl | Mods.Alt)]
    [InlineData("Delete", "Delete", Mods.None)]
    public void Chords_round_trip(string text, string key, Mods mods)
    {
        Assert.True(KeyChord.TryParse(text, out var c));
        Assert.Equal(new KeyChord(key, mods), c);
        Assert.True(KeyChord.TryParse(c.ToString(), out var again));
        Assert.Equal(c, again);
    }

    [Theory]
    [InlineData("Hyper+X")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+$")]
    public void Bad_chords_are_rejected(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void Display_is_human_readable()
    {
        Assert.Equal("Ctrl + Shift + S", new KeyChord("S", Mods.Ctrl | Mods.Shift).Display);
        Assert.Equal("Ctrl + 1", new KeyChord("D1", Mods.Ctrl).Display);
        Assert.Equal("Ctrl + ,", new KeyChord("OemComma", Mods.Ctrl).Display);
        Assert.Equal("Unassigned", KeyChord.None.Display);
    }

    [Fact]
    public void Conflicts_are_detected_within_a_context_only()
    {
        var b = new KeyBindings();
        var conflict = b.ConflictsWith("edit.duplicate", new KeyChord("W", Mods.None));
        Assert.Equal("tool.move", Assert.Single(conflict).Id);          // same context
        Assert.DoesNotContain(b.ConflictsWith("fly.forward", new KeyChord("Q", Mods.None)), a => a.Id == "tool.select"); // fly vs editor
        b.Set("edit.duplicate", new KeyChord("W", Mods.None));
        Assert.Single(b.AllConflicts());
    }

    [Fact]
    public void Reserved_keys_are_refused()
    {
        Assert.True(KeyBindings.IsReserved(new KeyChord("Escape", Mods.None), ShortcutContext.Editor, out _));
        Assert.True(KeyBindings.IsReserved(new KeyChord("F4", Mods.Alt), ShortcutContext.Editor, out _));
        Assert.True(KeyBindings.IsReserved(new KeyChord("LeftShift", Mods.Shift), ShortcutContext.Editor, out _));
        Assert.True(KeyBindings.IsReserved(new KeyChord("W", Mods.Ctrl), ShortcutContext.Fly, out _));
        Assert.False(KeyBindings.IsReserved(new KeyChord("K", Mods.Ctrl | Mods.Alt), ShortcutContext.Editor, out _));
    }

    [Fact]
    public void Reset_and_profiles_round_trip()
    {
        var b = new KeyBindings();
        b.Set("tool.move", new KeyChord("M", Mods.None));
        b.Set("edit.spawnWlf", new KeyChord("K", Mods.Ctrl | Mods.Alt));
        var profile = b.ExportProfile();

        var c = new KeyBindings();
        Assert.Empty(c.ImportProfile(profile));
        Assert.Equal("M", c.Get("tool.move").ToString());
        Assert.Equal("Ctrl+Alt+K", c.Get("edit.spawnWlf").ToString());

        c.Reset("tool.move");
        Assert.Equal("W", c.Get("tool.move").ToString());
        c.ResetAll();
        Assert.True(c.IsDefault("edit.spawnWlf"));
        Assert.Throws<InvalidDataException>(() => c.ImportProfile("{\"Format\":\"other\",\"Version\":1,\"Bindings\":{}}"));
    }

    [Fact]
    public void Stored_garbage_is_skipped_not_fatal()
    {
        var b = new KeyBindings();
        var problems = b.Load(new Dictionary<string, string> { ["nope"] = "X", ["tool.move"] = "Ctrl+$", ["tool.scale"] = "Escape", ["tool.rotate"] = "T" });
        Assert.Equal(3, problems.Count);
        Assert.Equal("T", b.Get("tool.rotate").ToString());
        Assert.Equal("W", b.Get("tool.move").ToString());
    }
}

public class EditorSettingsTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "yooos-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        var s = new EditorSettings();
        s.Viewport.FlySpeed = 12.5;
        s.Appearance.UiScale = 1.25;
        s.Keybindings["tool.move"] = "M";
        s.State["favorites"] = "a\nb";
        s.Save(path);
        var t = EditorSettings.Load(path, null, out var note);
        Assert.Null(note);
        Assert.Equal(12.5, t.Viewport.FlySpeed);
        Assert.Equal(1.25, t.Appearance.UiScale);
        Assert.Equal("M", t.Keybindings["tool.move"]);
        Assert.Equal("a\nb", t.State["favorites"]);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Legacy_flat_file_is_migrated()
    {
        var dir = TempDir();
        var legacy = Path.Combine(dir, "old.json");
        File.WriteAllText(legacy, "{\"buildDir\":\"X:\\\\game\",\"camera.speed\":\"14\",\"camera.fov\":\"80\",\"thumbs.auto\":\"0\",\"browser.view\":\"grid\",\"favorites\":\"k1\"}");
        var s = EditorSettings.Load(Path.Combine(dir, "settings.json"), legacy, out var note);
        Assert.NotNull(note);
        Assert.Equal(14, s.Viewport.FlySpeed);
        Assert.Equal(80, s.Viewport.FieldOfView);
        Assert.False(s.Performance.AutoThumbnails);
        Assert.Equal("grid", s.Catalogue.DefaultView);
        Assert.Equal("X:\\game", s.State["buildDir"]);
    }

    [Fact]
    public void Corrupt_or_out_of_range_values_are_safe()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var s = EditorSettings.Load(path, null, out var note);
        Assert.NotNull(note);
        Assert.True(File.Exists(path + ".bad"));
        Assert.Equal(1.0, s.Appearance.UiScale);

        File.WriteAllText(path, "{\"version\":1,\"appearance\":{\"uiScale\":9},\"viewport\":{\"fieldOfView\":5,\"defaultTool\":\"laser\"}}");
        s = EditorSettings.Load(path, null, out _);
        Assert.Equal(1.6, s.Appearance.UiScale);
        Assert.Equal(40, s.Viewport.FieldOfView);
        Assert.Equal("move", s.Viewport.DefaultTool);
    }
}

// Shares the process-wide ChunkCache with the game-file tests: never run in parallel with them.
[Collection("Shared chunk cache")]
public class ChunkCacheTests
{
    [Fact]
    public void Budget_bounds_decoded_chunks_across_archives()
    {
        // Two tiny DSAR-less files are enough to own reader identities; chunks are injected directly.
        var a = Path.GetTempFileName(); var b = Path.GetTempFileName();
        File.WriteAllBytes(a, new byte[64]); File.WriteAllBytes(b, new byte[64]);
        var old = FactionsReborn.WorldEditor.Core.Archives.ChunkCache.BudgetBytes;
        try
        {
            FactionsReborn.WorldEditor.Core.Archives.ChunkCache.Clear();
            FactionsReborn.WorldEditor.Core.Archives.ChunkCache.BudgetBytes = 1000;
            using var ra = new FactionsReborn.WorldEditor.Core.Archives.DsarReader(a);
            using var rb = new FactionsReborn.WorldEditor.Core.Archives.DsarReader(b);
            var add = typeof(FactionsReborn.WorldEditor.Core.Archives.ChunkCache).GetMethod("Add", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            for (int i = 0; i < 10; i++) add.Invoke(null, [i % 2 == 0 ? ra : rb, i, new byte[300]]);
            Assert.True(FactionsReborn.WorldEditor.Core.Archives.ChunkCache.Bytes <= 1000);
            Assert.Equal(900, FactionsReborn.WorldEditor.Core.Archives.ChunkCache.Bytes); // the 3 most recent chunks
        }
        finally
        {
            FactionsReborn.WorldEditor.Core.Archives.ChunkCache.BudgetBytes = old;
            FactionsReborn.WorldEditor.Core.Archives.ChunkCache.Clear();
            File.Delete(a); File.Delete(b);
        }
    }
}

public class CatalogTests
{
    [Theory]
    [InlineData("", 0xCBF29CE484222325UL)]   // FNV-1a 64 offset basis
    [InlineData("a", 0xAF63DC4C8601EC8CUL)]  // published FNV-1a 64 test vector
    [InlineData("foobar", 0x85944171F73967E8UL)]
    public void Sid_is_fnv1a64(string text, ulong expected) =>
        Assert.Equal(expected, FactionsReborn.WorldEditor.Core.Assets.Sid.Fnv1a64(text));

    [Theory]
    [InlineData("rifle-remington-bolt", "Weapons")]
    [InlineData("abby-80s-body", "Characters")]
    [InlineData("stalkermilitia-male-01-legs", "Characters")]
    [InlineData("truck-pickup-a", "Vehicles")]
    [InlineData("scar-wood-crate", "Characters")]
    [InlineData("bucket-rusty-a", "Props")]
    public void Groups_are_name_based(string name, string group) =>
        Assert.Equal(group, FactionsReborn.WorldEditor.Core.Assets.AssetGroups.Of(name, "Props"));
}
