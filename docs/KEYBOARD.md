# Keyboard shortcuts

All shortcuts can be changed in **Settings › Controls** (Ctrl + ,). Changes save automatically.

## Defaults

| Action | Default | Category |
|---|---|---|
| Select tool | Q | Tools |
| Move tool | W | Tools |
| Rotate tool | E | Tools |
| Scale tool | R | Tools |
| Draw arena boundary | B | Tools |
| Undo | Ctrl + Z | Edit |
| Redo | Ctrl + Y | Edit |
| Duplicate selection | Ctrl + D | Edit |
| Delete selection | Delete | Edit |
| Add WLF spawn | (unassigned) | Edit |
| Add Seraphite spawn | (unassigned) | Edit |
| New scene | Ctrl + N | File |
| Open scene | Ctrl + O | File |
| Save scene | Ctrl + S | File |
| Save scene as | Ctrl + Shift + S | File |
| Focus selected object | F | Camera |
| Frame everything | Shift + F | Camera |
| Reset camera | Home | Camera |
| Toggle grid | G | View |
| Toggle Asset Browser | Shift + A | View |
| Toggle Inspector | Shift + I | View |
| Toggle Scene Hierarchy | Shift + H | View |
| Maximise viewport | F11 | View |
| Shortcut sheet | F1 | View |
| Asset Catalogue / World Editor | Ctrl + 1 / Ctrl + 2 | Navigation |
| Settings | Ctrl + , | Navigation |
| Search assets | Ctrl + F | Navigation |
| Fly forward / back / left / right / up / down | W / S / A / D / E / Q (while holding the right mouse button) | Camera fly |

Fixed (not rebindable): **Escape** (cancel rebinding, finish the boundary, leave a text field, deselect),
Ctrl while dragging (snap), Shift while scaling (uniform), Shift while flying (faster), Alt + left drag (orbit).

## Rebinding

1. Click the shortcut. It shows **Press any key…**
2. Press the new combination: a key alone, or with Ctrl, Shift, Alt, Ctrl + Shift or Ctrl + Alt.
3. If another action already uses it, choose **Replace** (the other action becomes unassigned),
   **Swap** (the other action takes your old shortcut) or **Cancel**.
4. **Escape** cancels listening. Use **×** to unassign, **Reset** for one action, **Reset all** for everything.

Reserved and refused: Escape, Tab, Alt + F4, Alt + Space, F10, Ctrl + Alt + Delete, Print Screen, and modifier
keys on their own. Fly keys cannot use modifiers (they are held while moving).

## Rules while typing

When a text field has focus, only shortcuts with Ctrl or Alt (and F-keys) run, and never the text-editing
ones (Ctrl + A / C / V / X / Z / Y, arrows, Home, End, Delete…). So typing "w" in the search box types "w".

## Profiles

**Export…** writes every binding to a JSON file; **Import…** loads one (unknown actions and invalid or
reserved keys are skipped and reported).

```json
{ "Format": "yooos-keybindings", "Version": 1, "Bindings": { "tool.move": "W", "edit.undo": "Ctrl+Z" } }
```

Key names are WPF `System.Windows.Input.Key` names (`D1` for the 1 key, `OemComma` for the comma, `Delete`, `F11`…).
