using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FactionsReborn.WorldEditor.Core.Editor;
using Microsoft.Win32;

namespace FactionsReborn.WorldEditor;

/// <summary>
/// Settings > Controls: the keyboard shortcut manager. Click a binding to listen ("Press any key…"), Escape cancels,
/// reserved chords are refused, conflicts are shown inline with Replace / Swap / Cancel. Changes save automatically.
/// </summary>
public partial class MainWindow
{
    private string _shortcutFilter = "";
    private string _shortcutCategory = "All";
    private StackPanel? _shortcutList;
    private Button? _listeningButton;
    private bool _confirmResetAll;

    private void BuildControlsPage()
    {
        PageHeader("Controls", "Keyboard shortcuts. Click a shortcut, then press the new key combination.");

        // Toolbar: search, category, profile actions.
        var bar = new Grid { Margin = new Thickness(0, 12, 0, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchHost = new Grid { MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left, Width = 340 };
        var search = new TextBox { Text = _shortcutFilter, Padding = new Thickness(26, 6, 6, 6), ToolTip = "Search actions or keys" };
        var glyph = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(9, 0, 0, 0), Foreground = Res("Text3"), IsHitTestVisible = false };
        var hint = new TextBlock { Text = "Search actions or keys", Margin = new Thickness(27, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Text4"), IsHitTestVisible = false, Visibility = _shortcutFilter.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        search.TextChanged += (_, _) => { _shortcutFilter = search.Text; hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RebuildShortcutList(); };
        searchHost.Children.Add(search); searchHost.Children.Add(glyph); searchHost.Children.Add(hint);
        bar.Children.Add(searchHost);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var import = new Button { Content = "Import…", Margin = new Thickness(0, 0, 6, 0), ToolTip = "Load a keybinding profile (.json)" };
        import.Click += (_, _) => ImportBindings();
        var export = new Button { Content = "Export…", Margin = new Thickness(0, 0, 6, 0), ToolTip = "Save all bindings to a profile (.json)" };
        export.Click += (_, _) => ExportBindings();
        var resetAll = new Button { Content = _confirmResetAll ? "Click again to reset all" : "Reset all", ToolTip = "Restore every default shortcut" };
        if (_confirmResetAll) resetAll.Foreground = Res("AccentHi");
        resetAll.Click += (_, _) =>
        {
            if (!_confirmResetAll) { _confirmResetAll = true; BuildSettingsPage(); return; }
            _confirmResetAll = false;
            _bindings.ResetAll();
            Status("All shortcuts restored to their defaults.");
            BuildSettingsPage();
        };
        actions.Children.Add(import); actions.Children.Add(export); actions.Children.Add(resetAll);
        Grid.SetColumn(actions, 1);
        bar.Children.Add(actions);
        SettingsContent.Children.Add(bar);

        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var cat in new[] { "All" }.Concat(ShortcutCatalog.All.Select(a => a.Category).Distinct()))
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "ShortcutCat", Content = cat, IsChecked = cat == _shortcutCategory };
            chip.Checked += (_, _) => { _shortcutCategory = cat; RebuildShortcutList(); };
            chips.Children.Add(chip);
        }
        SettingsContent.Children.Add(chips);

        _shortcutList = new StackPanel();
        SettingsContent.Children.Add(_shortcutList);
        SettingsContent.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 18, 0, 0),
            Text = "Supported: single keys, Ctrl, Shift, Alt, Ctrl + Shift, Ctrl + Alt. Shortcuts without Ctrl or Alt are ignored while typing in a text field. "
                 + "Escape, Tab, Alt + F4, F10 and modifier keys on their own are reserved. Fly keys are read only while the right mouse button is held, so they may share keys with tools.",
        });
        RebuildShortcutList();
    }

    private void RebuildShortcutList()
    {
        if (_shortcutList is null) return;
        CancelCapture();
        _shortcutList.Children.Clear();

        var conflicts = _bindings.AllConflicts();
        if (conflicts.Count > 0)
        {
            var warn = new Border { BorderBrush = Res("AccentLo"), BorderThickness = new Thickness(1), Background = Res("AccentWash"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 12, 0, 0) };
            warn.Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Res("AccentHi"),
                Text = "Conflicts: " + string.Join(" · ", conflicts.Select(c => $"{c.Chord.Display} → {string.Join(", ", c.Actions.Select(a => a.Name))}")) + ". Only the first action runs until resolved.",
            };
            _shortcutList.Children.Add(warn);
        }

        var words = _shortcutFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var conflictIds = conflicts.SelectMany(c => c.Actions).Select(a => a.Id).ToHashSet();
        var shown = ShortcutCatalog.All
            .Where(a => _shortcutCategory == "All" || a.Category == _shortcutCategory)
            .Where(a => words.All(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || a.Category.Contains(w, StringComparison.OrdinalIgnoreCase)
                                     || _bindings.Get(a.Id).Display.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (shown.Count == 0)
            _shortcutList.Children.Add(new TextBlock { Text = "No action matches.", Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 16, 0, 0) });
        foreach (var group in shown.GroupBy(a => a.Category))
        {
            AddHeading(_shortcutList, group.Key);
            foreach (var action in group) _shortcutList.Children.Add(ShortcutRow(action, conflictIds.Contains(action.Id)));
        }
    }

    private FrameworkElement ShortcutRow(ShortcutAction action, bool inConflict)
    {
        var host = new StackPanel();
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = action.Name, FontSize = 12.5 });
        if (action.Description is not null) label.Children.Add(new TextBlock { Text = action.Description, Style = (Style)FindResource("Dim"), FontSize = 10.5 });
        row.Children.Add(label);

        var chord = _bindings.Get(action.Id);
        var bind = new Button
        {
            Content = chord.Display, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5, 10, 5),
            FontFamily = (FontFamily)FindResource("Display"),
            Foreground = chord.IsEmpty ? Res("Text4") : inConflict ? Res("AccentHi") : Res("Text"),
            BorderBrush = inConflict ? Res("AccentLo") : Res("Line2"),
            ToolTip = "Click, then press the new shortcut. Escape cancels.",
        };
        var feedback = new TextBlock { Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 2, 0, 4), Visibility = Visibility.Collapsed };
        bind.Click += (_, _) => BeginCapture(action, bind, host, feedback);
        Grid.SetColumn(bind, 1);
        row.Children.Add(bind);

        var clear = new Button { Style = (Style)FindResource("Ghost"), ToolTip = "Unassign", IsEnabled = !chord.IsEmpty, Content = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 9 } };
        clear.Click += (_, _) => { _bindings.Set(action.Id, KeyChord.None); RebuildShortcutList(); };
        Grid.SetColumn(clear, 2);
        row.Children.Add(clear);

        var reset = new Button { Style = (Style)FindResource("Ghost"), Content = "Reset", FontSize = 11, IsEnabled = !_bindings.IsDefault(action.Id), ToolTip = $"Default: {action.Default.Display}" };
        reset.Click += (_, _) => { _bindings.Reset(action.Id); RebuildShortcutList(); };
        Grid.SetColumn(reset, 3);
        row.Children.Add(reset);

        host.Children.Add(row);
        host.Children.Add(feedback);
        return host;
    }

    // ---------------------------------------------------------------- listening mode

    private void BeginCapture(ShortcutAction action, Button button, StackPanel host, TextBlock feedback)
    {
        CancelCapture();
        _listeningButton = button;
        button.Content = "Press any key…";
        button.Foreground = Res("AccentHi");
        button.BorderBrush = Res("Accent");
        feedback.Visibility = Visibility.Collapsed;
        _captureKey = e =>
        {
            var key = RealKey(e);
            if (key == Key.Escape) { CancelCapture(); RebuildShortcutList(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                var mods = CurrentMods();
                button.Content = (mods == Mods.None ? "" : new KeyChord("…", mods).Display.Replace("…", "").TrimEnd(' ', '+') + " + ") + "…";
                return; // wait for the actual key
            }
            var chord = new KeyChord(key.ToString(), CurrentMods());
            if (chord.Mods == (Mods.Shift | Mods.Alt) || chord.Mods == (Mods.Ctrl | Mods.Shift | Mods.Alt))
            {
                ShowFeedback(feedback, "Use a single key or Ctrl, Shift, Alt, Ctrl + Shift or Ctrl + Alt.", danger: true);
                return;
            }
            if (KeyBindings.IsReserved(chord, action.Context, out var reason)) { ShowFeedback(feedback, $"{chord.Display}: {reason}", danger: true); return; }
            _captureKey = null;
            _listeningButton = null;
            var conflicts = _bindings.ConflictsWith(action.Id, chord);
            if (conflicts.Count == 0)
            {
                _bindings.Set(action.Id, chord);
                Status($"{action.Name}: {chord.Display}");
                RebuildShortcutList();
                return;
            }
            ShowConflict(action, chord, conflicts, host, button);
        };
    }

    private void ShowFeedback(TextBlock feedback, string text, bool danger)
    {
        feedback.Text = text;
        feedback.Foreground = danger ? Res("Danger") : Res("Text3");
        feedback.Visibility = Visibility.Visible;
    }

    /// <summary>Inline resolution: Replace (other actions lose the chord), Swap (they take this action's old chord), Cancel.</summary>
    private void ShowConflict(ShortcutAction action, KeyChord chord, IReadOnlyList<ShortcutAction> others, StackPanel host, Button button)
    {
        button.Content = chord.Display;
        var old = _bindings.Get(action.Id);
        var panel = new Border { BorderBrush = Res("AccentLo"), BorderThickness = new Thickness(1), Background = Res("AccentWash"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 2, 0, 6) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Res("AccentHi"), Text = $"{chord.Display} is already used by {string.Join(", ", others.Select(o => o.Name))}." });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var replace = new Button { Style = (Style)FindResource("Primary"), Content = "Replace", Margin = new Thickness(0, 0, 6, 0), ToolTip = $"{string.Join(", ", others.Select(o => o.Name))} become unassigned" };
        replace.Click += (_, _) =>
        {
            foreach (var o in others) _bindings.Set(o.Id, KeyChord.None);
            _bindings.Set(action.Id, chord);
            Status($"{action.Name}: {chord.Display} ({string.Join(", ", others.Select(o => o.Name))} unassigned)");
            RebuildShortcutList();
        };
        buttons.Children.Add(replace);
        if (others.Count == 1 && !old.IsEmpty && !KeyBindings.IsReserved(old, others[0].Context, out _))
        {
            var swap = new Button { Content = $"Swap ({others[0].Name} → {old.Display})", Margin = new Thickness(0, 0, 6, 0) };
            swap.Click += (_, _) =>
            {
                _bindings.Set(others[0].Id, old);
                _bindings.Set(action.Id, chord);
                Status($"Swapped: {action.Name} = {chord.Display}, {others[0].Name} = {old.Display}");
                RebuildShortcutList();
            };
            buttons.Children.Add(swap);
        }
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => RebuildShortcutList();
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        panel.Child = stack;
        host.Children.Add(panel);
    }

    private void CancelCapture()
    {
        _captureKey = null;
        _listeningButton = null;
    }

    // ---------------------------------------------------------------- profiles

    private void ExportBindings()
    {
        var dlg = new SaveFileDialog { Filter = "Keybinding profile (*.json)|*.json", FileName = "yooos-keybindings.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, _bindings.ExportProfile()); Status($"Shortcuts exported to {dlg.FileName}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status($"Export failed: {ex.Message}"); }
    }

    private void ImportBindings()
    {
        var dlg = new OpenFileDialog { Filter = "Keybinding profile (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var problems = _bindings.ImportProfile(File.ReadAllText(dlg.FileName));
            Status(problems.Count == 0 ? $"Shortcuts imported from {Path.GetFileName(dlg.FileName)}." : $"Imported with {problems.Count} entr(ies) skipped: {string.Join("; ", problems.Take(3))}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Status($"Import failed: {ex.Message}");
        }
        BuildSettingsPage();
    }
}
