using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Editor;
using FactionsReborn.WorldEditor.Rendering;

namespace FactionsReborn.WorldEditor;

/// <summary>
/// --export-catalog &lt;dir&gt; [--thumbnails] [--turntables] [--thumb-size N] [--turn-size N] [--turn-frames N]
/// Writes &lt;dir&gt;/assets.json for the public asset site. Metadata only by default.
/// --thumbnails renders &lt;dir&gt;/thumbs/&lt;sid&gt;.jpg; --turntables renders &lt;dir&gt;/turntables/&lt;sid&gt;.jpg
/// (a strip of views all around the object; implies --thumbnails). Images come from the user's own install and
/// are derived from game content: publishing them is the site owner's decision. Existing images are reused, so an
/// interrupted export resumes. A log is written to &lt;dir&gt;/export.log; exit code 0 on success.
/// </summary>
public static class CatalogExport
{
    public sealed record Options(bool Thumbnails, bool Turntables, int ThumbSize = 192, int TurnSize = 160, int TurnFrames = 24);

    public static async Task<int> RunAsync(string dir, Options o)
    {
        Directory.CreateDirectory(dir);
        await using var log = new StreamWriter(Path.Combine(dir, "export.log"), append: false) { AutoFlush = true };
        void Log(string s) { lock (log) log.WriteLine($"{DateTime.Now:HH:mm:ss} {s}"); }
        try
        {
            var settings = EditorSettings.Load(Path.Combine(EditorSettings.DefaultDirectory, EditorSettings.FileName), EditorSettings.LegacyFile, out _);
            var buildDir = GameLocator.FindBuildDir(settings.State.GetValueOrDefault("buildDir"))
                ?? throw new DirectoryNotFoundException("TLOU Part II not found (set TLOU2_DIR or the game folder in Settings).");
            var version = typeof(CatalogExport).Assembly.GetName().Version?.ToString(3) ?? "0";
            var sw = Stopwatch.StartNew();
            var doc = await Task.Run(() => CatalogBuilder.Build(buildDir, $"Yooos TLOU Editor {version}", new Progress<string>(Log)));
            Log($"Catalogue: {doc.Counts["actors"]} actors ({doc.Counts["withMesh"]} with mesh), {doc.Counts["levels"]} levels in {sw.Elapsed.TotalSeconds:0.0} s");

            if (o.Thumbnails || o.Turntables) await RenderImagesAsync(dir, buildDir, doc, o, Log);

            var json = Path.Combine(dir, "assets.json");
            CatalogBuilder.Write(doc, json);
            Log($"Wrote {json} ({new FileInfo(json).Length / 1024:N0} KB) in {sw.Elapsed.TotalSeconds:0.0} s total");
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// WPF renders 3D in software on the calling thread, so images are rendered by several STA threads in parallel
    /// (each with its own dispatcher); decoded models are frozen and can be built on any thread.
    /// </summary>
    private static Task RenderImagesAsync(string dir, string buildDir, CatalogDocument doc, Options o, Action<string> log)
    {
        var thumbDir = Path.Combine(dir, "thumbs");
        var turnDir = Path.Combine(dir, "turntables");
        Directory.CreateDirectory(thumbDir);
        if (o.Turntables) Directory.CreateDirectory(turnDir);
        var background = new LinearGradientBrush(Color.FromRgb(0x1B, 0x20, 0x1C), Color.FromRgb(0x0C, 0x0E, 0x0C), 90);
        background.Freeze();
        var lib = new AssetLibrary(buildDir);
        var withMesh = doc.Assets.Where(a => a.Mesh is not null).ToList();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<CatalogEntry>(withMesh);
        int done = 0, thumbs = 0, turns = 0, failed = 0;
        int workers = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
        var sw = Stopwatch.StartNew();
        log($"Rendering images with {workers} threads…");

        void Work()
        {
            while (queue.TryDequeue(out var a))
            {
                string thumbFile = Path.Combine(thumbDir, a.Sid[2..] + ".jpg"), turnFile = Path.Combine(turnDir, a.Sid[2..] + ".jpg");
                try
                {
                    if (!File.Exists(thumbFile) || (o.Turntables && !File.Exists(turnFile)))
                    {
                        var asset = new Core.Scene.AssetRef(a.Archive, a.Path);
                        var pak = AssetLibrary.Decode(asset, lib.ReadRaw(asset));
                        var model = MeshBuilder.BuildPrototype(pak, null, cacheMaterials: false);
                        int[] subs = Core.Pak.GeometryReader.ActorLod0(pak.Geometry).Where(s => pak.Geometry.Submeshes[s].Indices.Length > 0).ToArray();
                        if (subs.Length == 0) throw new InvalidDataException("no LOD0 geometry");
                        var min = subs.Select(s => pak.Geometry.Submeshes[s].BoundsMin).Aggregate(System.Numerics.Vector3.Min);
                        var max = subs.Select(s => pak.Geometry.Submeshes[s].BoundsMax).Aggregate(System.Numerics.Vector3.Max);
                        if (!File.Exists(thumbFile)) SaveJpeg(ThumbnailRenderer.Render(model, min, max, o.ThumbSize, background), thumbFile, 82);
                        if (o.Turntables && !File.Exists(turnFile)) SaveJpeg(ThumbnailRenderer.RenderTurntable(model, min, max, o.TurnSize, o.TurnFrames, background), turnFile, 74);
                    }
                    if (File.Exists(thumbFile)) { a.Thumbnail = "thumbs/" + Path.GetFileName(thumbFile); Interlocked.Increment(ref thumbs); }
                    if (o.Turntables && File.Exists(turnFile)) { a.Turntable = "turntables/" + Path.GetFileName(turnFile); a.TurntableFrames = o.TurnFrames; Interlocked.Increment(ref turns); }
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or ArgumentException or IndexOutOfRangeException)
                {
                    Interlocked.Increment(ref failed);
                    log($"image {a.Name}: {ex.Message}");
                }
                int n = Interlocked.Increment(ref done);
                if (n % 250 == 0)
                {
                    double rate = n / Math.Max(1, sw.Elapsed.TotalSeconds);
                    log($"Images {n}/{withMesh.Count} · {rate:0.0}/s · ~{(withMesh.Count - n) / Math.Max(0.1, rate) / 60:0} min left");
                }
            }
        }

        var tasks = Enumerable.Range(0, workers).Select(_ =>
        {
            var tcs = new TaskCompletionSource();
            var t = new Thread(() => { try { Work(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } }) { IsBackground = true, Name = "catalog-render" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return tcs.Task;
        }).ToArray();
        return Task.WhenAll(tasks).ContinueWith(t =>
        {
            lib.Dispose();
            log($"Images: {thumbs} thumbnails, {turns} turntables, {failed} failed, {sw.Elapsed.TotalMinutes:0.0} min");
            t.GetAwaiter().GetResult();
        });
    }

    private static void SaveJpeg(BitmapSource bmp, string path, int quality)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0)));
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, overwrite: true); // never leave a half-written image that a resumed export would reuse
    }
}
