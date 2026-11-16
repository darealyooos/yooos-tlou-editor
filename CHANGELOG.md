# Changelog

All notable changes to this project are documented here. Format: [Keep a Changelog](https://keepachangelog.com),
versioning: [SemVer](https://semver.org).

## [0.9.0] — unreleased (first public preview)

### Added
- Public asset database site (`site/`, `scripts/build-site.ps1`): search by name, SID, archive or texture; categories,
  filters, sorting, grid / list, shareable URLs, per-asset sheet. Optional preview images and 360° turntables
  (24 pre-rendered views, drag to rotate, wheel / pinch to zoom, spin on hover) rendered from your own install.
- `--export-catalog <dir> [--thumbnails] [--turntables]`: catalogue export, parallel off-screen rendering, resumable.
- Complete interface redesign ("Dusk" design system): Asset Catalogue, World Editor and Settings workspaces,
  resizable panels, status bar with scene, selection, loading, camera and FPS.
- Settings workspace: General, Appearance, Controls, Viewport, Asset Catalogue, Performance; versioned JSON in
  `%LOCALAPPDATA%\YooosTLOUEditor`, imported once from the previous World Editor preferences.
- Keyboard shortcut manager: every action rebindable, listening mode, conflicts with Replace / Swap,
  reserved keys, search and filters, reset one / all, profile import / export. Centralised input routing
  that leaves text fields alone.
- Undo / redo for scene edits; Select tool (Q); toggle Asset Browser / Hierarchy / Inspector (Shift + A / H / I);
  reset camera (Home); Alt + left mouse orbit; smooth zoom; configurable fly speed, sensitivity, invert Y, FOV.
- Asset browser: name-based categories (Characters, Weapons, Vehicles…), favourites, sorting, list / grid,
  rendered thumbnails with an on-disk cache, "no mesh" state, orbitable preview.
- Autosave of a recovery copy (never overwrites your scene), optional reopen-last-scene at startup.
- `--profile` measurement mode.
- Web editor: Yooos styling, response compression, cache headers, bounded decode concurrency with cancellation.

### Changed
- Licensed under the MIT licence (© 2026 Yooos).
- Executables renamed to `YooosTLOUEditor.exe` and `YooosTLOUEditor.Web.exe`.

### Fixed
- Skinned character meshes were invisible (all vertices at the origin): vertex streams without the bit-packed flag
  store float32 positions and float16 UVs, and are now decoded (affects the editor, previews and thumbnails).
- Opening some levels (e.g. sea-hub-hotel-street) sent the camera ~1e43 m away so nothing was visible: map bounds
  now come from decoded LOD0 geometry and ignore prototypes whose header bounds hold sentinel values (23 of 322
  there); camera framing is clamped to 2.5 km.
- Memory retained while browsing: previews and thumbnails no longer pin decoded paks or cache their textures,
  and the archive chunk cache is a single 64 MB budget instead of up to 65 chunks per open archive.
  Measured: +172 MB after 120 previews before; bounded at ≈100 MB regardless of how many assets are browsed.
- Web editor: archive names are validated against the index (a crafted `archive` parameter could reach files
  outside the game folder); web mesh/texture caches are bounded and evicted templates are disposed.
- Self-test crashed when the viewport had no size at startup.
