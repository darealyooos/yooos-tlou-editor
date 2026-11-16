using System.IO;
using System.Windows;

namespace FactionsReborn.WorldEditor;

/// <summary>
/// Usage: YooosTLOUEditor.exe [scene.frscene] [--snapshot out.png] [--selftest out.frscene] [--profile out.txt]
///        YooosTLOUEditor.exe --export-catalog &lt;dir&gt; [--thumbnails] [--turntables] [--thumb-size 192] [--turn-size 160] [--turn-frames 24]
/// --snapshot renders the viewport once everything is loaded, writes the PNG and exits (used for automated checks).
/// --export-catalog writes the public asset site's data (see CatalogExport) without opening the editor window.
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int exportAt = Array.IndexOf(e.Args, "--export-catalog");
        if (exportAt >= 0 && exportAt + 1 < e.Args.Length)
        {
            int Arg(string name, int fallback, int min, int max)
            {
                int at = Array.IndexOf(e.Args, name);
                return at >= 0 && at + 1 < e.Args.Length && int.TryParse(e.Args[at + 1], out var v) ? Math.Clamp(v, min, max) : fallback;
            }
            bool turntables = e.Args.Contains("--turntables");
            var options = new CatalogExport.Options(
                Thumbnails: turntables || e.Args.Contains("--thumbnails"), Turntables: turntables,
                ThumbSize: Arg("--thumb-size", 192, 64, 512), TurnSize: Arg("--turn-size", 160, 64, 384), TurnFrames: Arg("--turn-frames", 24, 8, 72));
            int code = await CatalogExport.RunAsync(Path.GetFullPath(e.Args[exportAt + 1]), options);
            Shutdown(code);
            return;
        }

        var window = new MainWindow();
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--snapshot" && i + 1 < e.Args.Length) window.SnapshotPath = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i] == "--selftest" && i + 1 < e.Args.Length) window.SelfTestPath = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i] == "--profile" && i + 1 < e.Args.Length) window.ProfilePath = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i].EndsWith(".frscene", StringComparison.OrdinalIgnoreCase)) window.StartupScene = Path.GetFullPath(e.Args[i]);
        }
        MainWindow = window;
        window.Show();
    }
}
