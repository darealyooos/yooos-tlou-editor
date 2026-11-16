# Contributing

Thanks for helping improve Yooos TLOU Editor.

## Ground rules

1. **No game content in the repository.** No archives, paks, extracted models/textures/audio, and no
   screenshots that show game content. Tests that need game files read the contributor's own install and
   must return early when it is not found.
2. **No fake features.** If something is not implemented, the UI says so ("not decoded yet", "unavailable").
3. **Keep compatibility.** `.frscene` files and keybinding profiles are versioned; never break old files.
4. **Settings live in `%LOCALAPPDATA%\YooosTLOUEditor`**, never in the game folder.

## Workflow

```powershell
.\scripts\build.ps1           # must pass with warnings as errors
.\scripts\test.ps1            # unit tests
.\scripts\test.ps1 -SelfTest  # end-to-end editor test, if you touched the editor and have the game
```

- One topic per pull request, with a short "how it was verified" section.
- New shortcuts: add the action to `src/Core/Editor/KeyBindings.cs` (`ShortcutCatalog`) and its handler in
  `src/App/MainWindow.Input.cs` (`RegisterActions`). The app refuses to start if a catalogued action has no
  handler, and a unit test checks the defaults have no conflicts.
- New settings: add the property to `src/Core/Editor/EditorSettings.cs`, clamp it in `Normalise()`, add a row
  in `src/App/MainWindow.Settings.cs`, and make sure it has a real effect.
- UI: use the tokens and styles in `src/App/Themes/Theme.xaml`; do not hard-code colours.

## Code style

C# latest, nullable enabled, file-scoped namespaces, `async` for I/O and decoding off the UI thread.
Keep Core free of WPF so it stays testable.
