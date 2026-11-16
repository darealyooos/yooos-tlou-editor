using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FactionsReborn.WorldEditor.Core.Editor;

namespace FactionsReborn.WorldEditor;

/// <summary>
/// The single place where keyboard shortcuts are handled. Every action is registered once with its handler;
/// bindings come from <see cref="KeyBindings"/> (user-editable in Settings > Controls).
/// Rules: rebinding swallows input; mouse-look reserves the fly keys; text fields keep normal typing.
/// </summary>
public partial class MainWindow
{
    private readonly KeyBindings _bindings = new();
    private Dictionary<string, Action> _actions = [];
    /// <summary>Set while Settings > Controls is listening for a new chord; receives the raw key event.</summary>
    private Action<KeyEventArgs>? _captureKey;

    private void RegisterActions()
    {
        _actions = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            ["tool.select"] = () => SetTool(null),
            ["tool.move"] = () => SetTool(Rendering.GizmoMode.Move),
            ["tool.rotate"] = () => SetTool(Rendering.GizmoMode.Rotate),
            ["tool.scale"] = () => SetTool(Rendering.GizmoMode.Scale),
            ["tool.boundary"] = () => SetBoundaryTool(ToolBoundary.IsChecked != true),
            ["edit.undo"] = Undo,
            ["edit.redo"] = Redo,
            ["edit.duplicate"] = () => OnDuplicate(null, null),
            ["edit.delete"] = () => OnDelete(null, null),
            ["edit.spawnWlf"] = () => AddSpawn(Core.Scene.Team.WLF),
            ["edit.spawnScars"] = () => AddSpawn(Core.Scene.Team.Seraphites),
            ["file.new"] = () => OnNewScene(this, new RoutedEventArgs()),
            ["file.open"] = () => OnOpenScene(null, null),
            ["file.save"] = () => OnSaveScene(null, null),
            ["file.saveAs"] = () => SaveScene(null),
            ["view.focus"] = () => OnFocus(null, null),
            ["view.frameAll"] = () => OnFrameAll(null, null),
            ["view.resetCamera"] = ResetCamera,
            ["view.grid"] = () => { ShowGrid.IsChecked = !ShowGrid.IsChecked; ApplyLayerVisibility(); },
            ["view.browser"] = () => TogglePanel(PanelKind.Browser),
            ["view.inspector"] = () => TogglePanel(PanelKind.Inspector),
            ["view.hierarchy"] = () => TogglePanel(PanelKind.Hierarchy),
            ["view.maximise"] = () => SetMaximised(!_maximised),
            ["view.shortcuts"] = () => OnToggleShortcuts(null, null),
            ["nav.catalogue"] = () => SetWorkspace(Workspace.Catalogue),
            ["nav.world"] = () => { SetWorkspace(Workspace.World); ViewportHost.Focus(); },
            ["nav.settings"] = () => SetWorkspace(Workspace.Settings),
            ["browser.search"] = () => { if (_workspace == Workspace.Settings) SetWorkspace(Workspace.World); SearchBox.Focus(); SearchBox.SelectAll(); },
        };
        // Every catalogued editor action must have a handler (fly actions are polled per frame).
        foreach (var a in ShortcutCatalog.All)
            if (a.Context == ShortcutContext.Editor && !_actions.ContainsKey(a.Id))
                throw new InvalidOperationException($"No handler for shortcut action {a.Id}");
        _bindings.Changed += RefreshShortcutLabels;
    }

    private static Mods CurrentMods()
    {
        var m = Mods.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) m |= Mods.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) m |= Mods.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) m |= Mods.Alt;
        return m;
    }

    /// <summary>The real key (Alt combinations arrive as Key.System; IME keys as ImeProcessed).</summary>
    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };

    private static KeyChord ChordOf(KeyEventArgs e) => new(RealKey(e).ToString(), CurrentMods());

    private static bool IsTextInput(object? focused) =>
        focused is TextBoxBase or PasswordBox || (focused is ComboBox { IsEditable: true });

    // Chords a focused text box handles itself; they never trigger editor actions while typing.
    private static readonly HashSet<string> TextEditingKeys = new(StringComparer.Ordinal)
    {
        "A", "C", "V", "X", "Z", "Y", "Left", "Right", "Up", "Down", "Home", "End", "Back", "Delete", "Insert", "Enter", "Return", "Space",
    };

    private static bool IsFunctionKey(string key) => key.Length is 2 or 3 && key[0] == 'F' && char.IsDigit(key[1]);

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_captureKey is not null) { _captureKey(e); e.Handled = true; return; }

        var key = RealKey(e);
        if (_looking)
        {
            // Mouse-look owns the keyboard: fly keys are polled each frame, everything else waits.
            if (key is not (Key.LeftShift or Key.RightShift)) e.Handled = true;
            return;
        }

        if (key == Key.Escape) { if (HandleEscape(e.OriginalSource)) e.Handled = true; return; }

        var chord = ChordOf(e);
        if (IsTextInput(Keyboard.FocusedElement))
        {
            bool command = chord.Mods.HasFlag(Mods.Ctrl) || chord.Mods.HasFlag(Mods.Alt) || IsFunctionKey(chord.Key);
            if (!command || ((chord.Mods & ~Mods.Shift) == Mods.Ctrl && TextEditingKeys.Contains(chord.Key))) return;
        }
        // Open popups/menus and drop-downs keep their own keyboard handling.
        if (Keyboard.FocusedElement is MenuItem || (Keyboard.FocusedElement is DependencyObject d && FindParent<ComboBox>(d) is { IsDropDownOpen: true })) return;

        var action = _bindings.Resolve(chord);
        if (action is null || !_actions.TryGetValue(action.Id, out var run)) return;
        if (_workspace == Workspace.Settings && action.Category is "Tools" or "Edit" or "Camera" && action.Id is not ("edit.undo" or "edit.redo")) return;
        run();
        e.Handled = true;
    }

    /// <summary>Escape: close the sheet, finish the boundary, leave a text field, or deselect.</summary>
    private bool HandleEscape(object? source)
    {
        if (ShortcutSheet.Visibility == Visibility.Visible) { ShortcutSheet.Visibility = Visibility.Collapsed; return true; }
        if (ToolBoundary.IsChecked == true) { SetBoundaryTool(false); return true; }
        if (IsTextInput(Keyboard.FocusedElement)) { if (_workspace == Workspace.World) ViewportHost.Focus(); return true; }
        if (_workspace == Workspace.World && _selected is not null) { Select(null); RebuildHierarchy(); return true; }
        return false;
    }

    /// <summary>Fly keys come from the bindings and are read as held keys while mouse-looking.</summary>
    private bool FlyKeyDown(string actionId)
    {
        var chord = _bindings.Get(actionId);
        return !chord.IsEmpty && Enum.TryParse<Key>(chord.Key, out var k) && Keyboard.IsKeyDown(k);
    }

    /// <summary>Tooltips, menu gesture texts and the shortcut sheet follow the current bindings.</summary>
    private void RefreshShortcutLabels()
    {
        string G(string id) => _bindings.Get(id).IsEmpty ? "" : _bindings.Get(id).Display.Replace(" + ", "+");
        if (MenuMove is null) return;
        MenuSelect.InputGestureText = G("tool.select");
        MenuMove.InputGestureText = G("tool.move");
        MenuRotate.InputGestureText = G("tool.rotate");
        MenuScale.InputGestureText = G("tool.scale");
        MenuBoundary.InputGestureText = G("tool.boundary");
        MenuUndo.InputGestureText = G("edit.undo");
        MenuRedo.InputGestureText = G("edit.redo");
        MenuDuplicate.InputGestureText = G("edit.duplicate");
        MenuDeleteItem.InputGestureText = G("edit.delete");
        MenuSpawnWlf.InputGestureText = G("edit.spawnWlf");
        MenuSpawnScars.InputGestureText = G("edit.spawnScars");
        MenuShortcuts.InputGestureText = G("view.shortcuts");
        MenuNew.InputGestureText = G("file.new");
        MenuOpen.InputGestureText = G("file.open");
        MenuSave.InputGestureText = G("file.save");
        MenuSaveAs.InputGestureText = G("file.saveAs");
        ShowGrid.InputGestureText = G("view.grid");
        MenuFocus.InputGestureText = G("view.focus");
        MenuFrameAll.InputGestureText = G("view.frameAll");
        MenuResetCamera.InputGestureText = G("view.resetCamera");
        MaximiseItem.InputGestureText = G("view.maximise");
        MenuToggleBrowser.InputGestureText = G("view.browser");
        MenuToggleHierarchy.InputGestureText = G("view.hierarchy");
        MenuToggleInspector.InputGestureText = G("view.inspector");
        KeySelect.Text = G("tool.select"); KeyMove.Text = G("tool.move"); KeyRotate.Text = G("tool.rotate");
        KeyScale.Text = G("tool.scale"); KeyBoundary.Text = G("tool.boundary");
        string fly = string.Join("", new[] { "fly.forward", "fly.left", "fly.back", "fly.right" }.Select(id => KeyChord.KeyLabel(_bindings.Get(id).Key)));
        HintFly.Text = fly;
        HintUpDown.Text = $"{KeyChord.KeyLabel(_bindings.Get("fly.down").Key)}/{KeyChord.KeyLabel(_bindings.Get("fly.up").Key)}";
        HintShortcuts.Text = G("view.shortcuts");
        NavCatalogue.ToolTip = $"Browse and inspect assets  ({G("nav.catalogue")})";
        NavWorld.ToolTip = $"Build the scene  ({G("nav.world")})";
        NavSettings.ToolTip = $"Preferences and keyboard shortcuts  ({G("nav.settings")})";
        BuildShortcutSheet();
    }
}
