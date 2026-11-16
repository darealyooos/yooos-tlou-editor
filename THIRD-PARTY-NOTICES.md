# Third-party notices

Yooos TLOU Editor ships the following third-party software.

| Component | Version | Used by | Licence |
|---|---|---|---|
| [three.js](https://threejs.org) (`three.module.js`, `OrbitControls.js`, `TransformControls.js`) | r169 | Web editor (`web/wwwroot/lib`) | MIT — full text in `web/wwwroot/lib/three-LICENSE.txt` |
| .NET runtime libraries (self-contained builds only) | 10.0 | Desktop and web editors | MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |

Development-only dependencies (not distributed in the release ZIP): xUnit, Microsoft.NET.Test.Sdk,
coverlet.collector — used by `tests/`.

Fonts: the interface uses Bahnschrift, Segoe UI, Segoe Fluent Icons / Segoe MDL2 Assets and Cascadia Mono
when they are installed with Windows. No font files are distributed.

## Game content

No content from The Last of Us Part II (archives, models, textures, audio, text, logos) is included in this
repository or in any release. The editor reads the user's own installation at run time; thumbnails it
renders are cached only on the user's PC (`%LOCALAPPDATA%\YooosTLOUEditor\thumbs`).
