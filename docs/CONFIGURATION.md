# Configuration

## Where things are stored

| What | Location |
|---|---|
| Preferences, key bindings, panel layout, favourites, game folder | `%LOCALAPPDATA%\YooosTLOUEditor\settings.json` |
| Thumbnail cache | `%LOCALAPPDATA%\YooosTLOUEditor\thumbs\*.png` (rendered from your install, never shared) |
| Scenes | `Documents\Factions Reborn\Scenes` (changeable in Settings › General) |
| Autosave recovery copies | `<Scenes>\Autosave\<scene>.autosave.frscene` |

The game installation directory is only ever read.

`settings.json` is written atomically (temporary file, then replace). If it cannot be read, defaults are used
and the old file is kept as `settings.json.bad`. Out-of-range values are clamped on load. Preferences from the
earlier "Factions Reborn World Editor" (`%LOCALAPPDATA%\FactionsReborn\WorldEditor\settings.json`) are imported
once if no new file exists.

## Settings reference

| Section | Setting | Effect |
|---|---|---|
| General | When the editor starts | Empty scene, or reopen the last scene |
| | Autosave recovery copy | Every N minutes (0 = off) while there are unsaved changes |
| | Scenes folder | Default folder for open / save dialogs |
| | Ask before discarding / deleting | Confirmation dialogs (deleting can always be undone) |
| | Game files | Change the install folder (applies after restart) |
| Appearance | UI scale | 80–160 % |
| | Spacing | Comfortable / compact asset rows |
| | Panels | Show or hide Asset Browser, Scene Hierarchy, Inspector; reset layout |
| Controls | Keyboard shortcuts | See [KEYBOARD.md](KEYBOARD.md) |
| Viewport | Fly speed, fast multiplier, look sensitivity, invert Y, zoom speed, smooth zoom, Alt-orbit, field of view | Camera navigation |
| | Ground grid, tool at startup | Defaults |
| Asset Catalogue | View mode, thumbnail size, sorting | Browser layout |
| | Preview the selected asset | Off: metadata only until placed |
| Performance | Render thumbnails for visible assets / keep on disk | Background thumbnail work and its cache |
| | Textured previews and thumbnails | Off: untextured, faster, less memory |
| | Preview meshes kept in memory | Size of the preview cache (2–128) |
| | Release paks not used by the scene | Frees decoded data after deletes, new / opened scenes; "Release now" forces it |

## File format (excerpt)

```json
{
  "version": 1,
  "general": { "startup": "empty", "autosaveMinutes": 5, "confirmDelete": false, "confirmDiscard": true, "language": "en" },
  "appearance": { "uiScale": 1.0, "density": "comfortable", "theme": "dusk", "showBrowser": true, "showHierarchy": true, "showInspector": true },
  "viewport": { "showGrid": true, "flySpeed": 8, "fastMultiplier": 5, "lookSensitivity": 1, "invertY": false, "zoomSpeed": 1, "smoothZoom": true, "altOrbit": true, "fieldOfView": 70, "defaultTool": "move" },
  "catalogue": { "defaultView": "list", "thumbnailSize": "medium", "defaultSort": 0, "autoPreview": true },
  "performance": { "autoThumbnails": true, "thumbnailDiskCache": true, "texturedPreviews": true, "previewCacheSize": 16, "trimUnusedPaks": true },
  "keybindings": { "tool.move": "M" },
  "state": { "buildDir": "…", "favorites": "…", "layout.left": "300", "lastScene": "…" }
}
```

Only overrides are stored under `keybindings`; everything else uses the defaults.
