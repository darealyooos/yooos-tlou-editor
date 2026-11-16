using System.Text.Json;
using FactionsReborn.WorldEditor.Core.Scene;

namespace FactionsReborn.WorldEditor;

/// <summary>
/// Undo/redo of scene edits (objects, spawns, boundary) as JSON snapshots in the .frscene serializer.
/// Opening maps and loading scenes are not undoable (they clear the history).
/// Each edit entry point calls <see cref="RecordUndo"/> before it mutates the document.
/// </summary>
public partial class MainWindow
{
    private const int UndoLimit = 100;
    private readonly LinkedList<(string Label, string State)> _undo = new();
    private readonly Stack<(string Label, string State)> _redo = new();
    private bool _restoring;

    private sealed class EditState
    {
        public List<SceneObject> Objects { get; set; } = [];
        public List<SpawnPoint> Spawns { get; set; } = [];
        public MapBoundary Boundary { get; set; } = new();
    }

    private string CaptureState() => JsonSerializer.Serialize(new EditState { Objects = _doc.Objects, Spawns = _doc.Spawns, Boundary = _doc.Boundary }, SceneDocument.Json);

    /// <summary>Snapshot before an edit. Consecutive identical snapshots are merged.</summary>
    private void RecordUndo(string label)
    {
        if (_restoring) return;
        var state = CaptureState();
        if (_undo.Last is { } last && last.Value.State == state) return;
        _undo.AddLast((label, state));
        while (_undo.Count > UndoLimit) _undo.RemoveFirst();
        _redo.Clear();
        UpdateUndoMenu();
    }

    private void ClearHistory() { _undo.Clear(); _redo.Clear(); UpdateUndoMenu(); }

    private void Undo()
    {
        if (_undo.Last is not { } node) { Status("Nothing to undo."); return; }
        _undo.RemoveLast();
        _redo.Push((node.Value.Label, CaptureState()));
        Restore(node.Value.State);
        Status($"Undo: {node.Value.Label}");
    }

    private void Redo()
    {
        if (_redo.Count == 0) { Status("Nothing to redo."); return; }
        var (label, state) = _redo.Pop();
        _undo.AddLast((label, CaptureState()));
        Restore(state);
        Status($"Redo: {label}");
    }

    private void Restore(string json)
    {
        var s = JsonSerializer.Deserialize<EditState>(json, SceneDocument.Json)!;
        _restoring = true;
        try
        {
            var selectedId = (_selected as SceneObject)?.Id ?? (_selected as SpawnPoint)?.Id;
            var keep = s.Objects.Select(o => o.Id).ToHashSet();
            foreach (var gone in _doc.Objects.Where(o => !keep.Contains(o.Id)).ToList())
            {
                if (_objectVisuals.Remove(gone.Id, out var v)) _objectRoot.Children.Remove(v);
                _objectPaks.Remove(gone.Id);
            }
            var existing = _doc.Objects.ToDictionary(o => o.Id);
            _doc.Objects.Clear();
            foreach (var o in s.Objects)
            {
                _doc.Objects.Add(o);
                // Same id and same asset: reuse the loaded mesh, only the transform changes.
                if (existing.TryGetValue(o.Id, out var old) && old.Asset == o.Asset && old.Prototype == o.Prototype && _objectVisuals.ContainsKey(o.Id))
                    UpdateObjectTransform(o);
                else
                {
                    if (_objectVisuals.Remove(o.Id, out var stale)) _objectRoot.Children.Remove(stale);
                    _ = CreateObjectVisualAsync(o);
                }
            }
            _doc.Spawns.Clear(); _doc.Spawns.AddRange(s.Spawns);
            _doc.Boundary = s.Boundary;
            object? reselect = selectedId is { } id ? (object?)_doc.Objects.FirstOrDefault(o => o.Id == id) ?? _doc.Spawns.FirstOrDefault(sp => sp.Id == id) : null;
            Select(reselect);
            MarkDirty();
            RebuildHelpers();
            RebuildHierarchy();
        }
        finally { _restoring = false; }
        UpdateUndoMenu();
    }

    private void UpdateUndoMenu()
    {
        if (MenuUndo is null) return;
        MenuUndo.IsEnabled = _undo.Count > 0;
        MenuRedo.IsEnabled = _redo.Count > 0;
        MenuUndo.Header = _undo.Last is { } u ? $"Undo {u.Value.Label}" : "Undo";
        MenuRedo.Header = _redo.Count > 0 ? $"Redo {_redo.Peek().Label}" : "Redo";
    }
}
