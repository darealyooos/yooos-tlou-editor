# Architecture

```
src/
  Core/            .NET 10 class library, no UI: everything that reads game files and editor data
    Archives/      PsarcArchive (TOC, zlib blocks, per-archive lock), DsarReader + ChunkCache (LZ4 chunks, global 64 MB budget)
    Pak/           PakFile (pages, pointer fixups), GeometryReader (GEOMETRY_1), TextureReader (VRAM_DESC), raycasts
    Textures/      BC1–5 block decoding
    Assets/        GameLocator, AssetIndex (scan of every psarc), AssetLibrary (thread-safe decoded-pak cache with Trim)
    Scene/         SceneDocument (.frscene), transforms, boundary
    Editor/        EditorSettings (versioned preferences), KeyBindings + ShortcutCatalog (rebindable shortcuts)
  App/             WPF desktop editor (YooosTLOUEditor.exe)
    Themes/Theme.xaml        design system: colour tokens, typography, every control template
    MainWindow.xaml          layout: navigation, browser, viewport / catalogue / settings, outliner + inspector, status bar
    MainWindow.*.cs          partial classes by concern:
      .xaml.cs     scene, maps, objects, files, memory trim
      .Browser.cs  asset browser, categories, favourites, list/grid, preview cache, thumbnails
      .Viewport.cs camera navigation, gizmo drags, picking, helpers rendering
      .Panels.cs   outliner and inspector
      .Input.cs    the single keyboard router (action registry)
      .Undo.cs     snapshot undo / redo
      .Settings.cs settings persistence, application and pages
      .Shortcuts.cs Settings › Controls (shortcut manager)
      .Layout.cs   workspaces, panels, menus, shortcut sheet
      .SelfTest.cs / .Profile.cs  automation (--selftest, --profile)
    Rendering/     CameraRig, Gizmo, MeshBuilder (frozen WPF models, per-material batching), Shapes
  Web/             ASP.NET Core minimal API + three.js front end (YooosTLOUEditor.Web.exe)
tests/Yooos.Tests  xUnit: formats, settings, key bindings, chunk cache; game-file tests when an install exists
```

## Data flow

1. `GameLocator` finds `build\pc\main`; `AssetIndex.Scan` lists actor97 and pak68 paks in every psarc (~170 ms warm).
2. Selecting an asset decodes it **off the UI thread** without pinning it: the pak is used if the scene already
   loaded it, otherwise decoded into a temporary. `MeshBuilder` turns LOD0 into frozen models (one per material).
3. Placing an asset loads it through `AssetLibrary` (cached while the scene uses it). Maps are merged per material.
4. `SceneDocument` is the single source of truth for objects, spawns and the boundary; undo snapshots serialise
   exactly those with the `.frscene` serializer.

## Input

`MainWindow.Input.cs` registers every action once (`RegisterActions`) and handles `PreviewKeyDown` for the whole
window: rebinding capture first, then mouse-look (fly keys are polled each frame from their bindings), Escape,
text-field rules, then `KeyBindings.Resolve(chord)`. No other code reads keys for shortcuts.

## Threading and memory

- Archive reads are serialised per archive (`PsarcArchive` lock); `ChunkCache` is global and locked.
- Decoding and mesh building run on the thread pool; WPF models are frozen so they can cross threads.
- Bounded caches: preview meshes (Settings), DSAR chunks (64 MB), web mesh templates (40) and textures (256).
  Decoded paks are kept only while the scene references them (`AssetLibrary.Trim`, `MeshBuilder.TrimMaterials`).
