using System.Diagnostics;
using System.IO;
using System.Windows;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Rendering;

namespace FactionsReborn.WorldEditor;

/// <summary>
/// --profile out.txt: measures startup, indexing, search, preview loading (and memory growth while browsing),
/// thumbnail rendering and map loading with the editor's real code paths, then exits.
/// </summary>
public partial class MainWindow
{
    public string? ProfilePath { get; set; }
    internal static readonly Stopwatch SinceProcessStart = Stopwatch.StartNew();
    private double _indexMs, _startupMs;

    private static long ManagedMb() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true) / (1024 * 1024); }
    private static long WorkingSetMb() { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.WorkingSet64 / (1024 * 1024); }

    private async Task RunProfileAsync(string outPath)
    {
        var log = new List<string>
        {
            $"startup_to_window_loaded_ms {_startupMs:0}",
            $"index_ms {_indexMs:0}  entries {_index.Count}",
            $"memory_after_index managed_mb {ManagedMb()} working_set_mb {WorkingSetMb()}",
        };

        // Search: the full filter + sort + bind path, as typed.
        foreach (var q in new[] { "b", "barrel", "wood crate", "zzzz-no-match", "" })
        {
            var sw = Stopwatch.StartNew();
            const int Runs = 20;
            for (int i = 0; i < Runs; i++) { SearchBox.Text = q; ApplySearchNow(); }
            log.Add($"search '{q}' avg_ms {sw.Elapsed.TotalMilliseconds / Runs:0.00} results {_shownItems.Count}");
        }
        SearchBox.Text = "";
        ApplySearchNow();

        // Preview loading over many different actors (the browsing pattern), then memory growth.
        var actors = _index.Where(e => e.Kind == AssetKind.Actor && e.Size < 4 << 20).Where((_, i) => i % (int.TryParse(Environment.GetEnvironmentVariable("YOOOS_PROFILE_STRIDE"), out var st) ? st : 37) == 0).Take(int.TryParse(Environment.GetEnvironmentVariable("YOOOS_PROFILE_PREVIEWS"), out var np) ? np : 120).ToList();

        long before = ManagedMb();
        var times = new List<double>();
        int ok = 0;
        foreach (var e in actors)
        {
            var item = _sourceItems.FirstOrDefault(i => i.Asset == e.Ref);
            if (item is null) continue;
            var sw = Stopwatch.StartNew();
            await LoadPreviewAsync(item, debounce: false);
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (_previewModel.Content is not null) ok++;
        }
        long after = ManagedMb();
        times.Sort();
        log.Add($"preview n {times.Count} with_mesh {ok} median_ms {times[times.Count / 2]:0} p90_ms {times[(int)(times.Count * 0.9)]:0}");
        log.Add($"preview_memory managed_mb before {before} after {after} growth {after - before} (preview cache {_previewCache.Count}, paks cached {_library?.CachedCount}, materials cached {MeshBuilder.CachedMaterials}, thumbs {_thumbs.Count}, dsar chunk cache {Core.Archives.ChunkCache.Bytes / 1048576} mb)");
        var keepModel = _previewModel.Content;
        _previewModel.Content = null;
        _previewCache.Clear();
        log.Add($"preview_memory_after_releasing_cache managed_mb {ManagedMb()}");
        _thumbs.Clear();
        foreach (var i in _sourceItems) i.Thumbnail = null;
        log.Add($"preview_memory_after_releasing_thumbnails managed_mb {ManagedMb()}");
        _previewModel.Content = keepModel;

        // Thumbnails: render path without the disk cache.
        var thumbItems = _sourceItems.Where(i => !i.IsLevel).Skip(500).Take(60).ToList();
        before = ManagedMb();
        var tsw = Stopwatch.StartNew();
        int rendered = 0;
        foreach (var item in thumbItems)
        {
            try
            {
                var (model, min, max) = await Task.Run(() => BuildThumbnailModel(item, null));
                RenderThumbnail(model, min, max);
                rendered++;
            }
            catch (InvalidDataException) { }
        }
        log.Add($"thumbnails n {thumbItems.Count} rendered {rendered} total_ms {tsw.Elapsed.TotalMilliseconds:0} per_item_ms {tsw.Elapsed.TotalMilliseconds / Math.Max(1, thumbItems.Count):0}");
        log.Add($"thumbnail_memory managed_mb before {before} after {ManagedMb()}");

        // Map loading + frame rate.
        var level = _index.FirstOrDefault(e => e.Kind == AssetKind.Level && e.Name == "sea-hub-hotel-street");
        if (level is not null)
        {
            var sw = Stopwatch.StartNew();
            await OpenMapAsync(level.Ref);
            log.Add($"open_map sea-hub-hotel-street ms {sw.Elapsed.TotalMilliseconds:0} tris {_maps.Sum(m => m.Triangles)}");
            int frames = 0;
            TimeSpan lastTime = default;
            void Count(object? s, EventArgs a)
            {
                if (a is System.Windows.Media.RenderingEventArgs r && r.RenderingTime == lastTime) return;
                if (a is System.Windows.Media.RenderingEventArgs r2) lastTime = r2.RenderingTime;
                frames++;
            }
            System.Windows.Media.CompositionTarget.Rendering += Count;
            var fsw = Stopwatch.StartNew();
            while (fsw.Elapsed.TotalSeconds < 4) { _camera.Look(1.5, 0); RefreshGizmo(); await Task.Delay(1); }
            System.Windows.Media.CompositionTarget.Rendering -= Count;
            log.Add($"viewport_fps_while_orbiting {frames / fsw.Elapsed.TotalSeconds:0}");
            log.Add($"memory_after_map managed_mb {ManagedMb()} working_set_mb {WorkingSetMb()}");
        }
        File.WriteAllLines(outPath, log);
    }
}
