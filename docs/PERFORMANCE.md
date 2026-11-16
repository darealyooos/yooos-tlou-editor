# Performance

## How to measure

```powershell
YooosTLOUEditor.exe --profile profile.txt
# optional: $env:YOOOS_PROFILE_PREVIEWS=360; $env:YOOOS_PROFILE_STRIDE=12  (longer browsing run)
```

The profile runs the editor's real code paths and writes: startup time, index time, search time per query
(filter + sort + bind), preview decode time (median / p90) over many actors, memory before / after browsing,
thumbnail render time, map load time, viewport frame rate while orbiting, and memory after loading a map.
Managed memory is measured after a full GC.

## Results on the development PC (2026-11-16)

Same machine, same game install, warm file cache. "Before" is the build before this optimisation pass.

| Metric | Before | After |
|---|---|---|
| Startup to window ready | 494 ms | 485–583 ms (noise) |
| Index 9 892 paks | 165 ms | 151–173 ms |
| Search (6 986 actors), per keystroke | 0.8–2.0 ms | 0.7–2.4 ms |
| Preview decode, median / p90 | 33 / 51 ms | 27–30 / 42–56 ms |
| **Managed memory retained after browsing 120 previews** | **+172 MB, growing with every archive visited** | **+97 MB, bounded** (+100 MB after 360 previews) |
| Thumbnail render | 20 ms each, +22 MB retained per 60 | 21 ms each, +1 MB |
| Managed memory after loading sea-hub-hotel-street (3.4 M triangles) | 448 MB | 339 MB |
| Working set after map | 1 400 MB | 1 161 MB |
| Viewport while orbiting that map | (not measured reliably before) | 163 fps |

Indexing and search were already fast, so they were left alone (an on-disk index cache would save < 200 ms).

## What changed

1. **Archive chunk cache** — each open archive kept up to 65 decoded LZ4 chunks; with ~40 archives open this
   retained hundreds of MB while browsing. Replaced by one global LRU with a 64 MB budget (`ChunkCache`).
2. **Previews do not pin paks** — previously every previewed asset stayed decoded in `AssetLibrary` forever.
   Previews now reuse a pak only if the scene loaded it, and otherwise decode a temporary; the meshes go into a
   bounded LRU (Settings › Performance).
3. **Textures of previews and thumbnails are not cached** in `MeshBuilder`'s shared material cache.
4. **Trim on scene changes** — after deleting objects, creating or opening a scene, decoded paks, meshes and
   materials that the scene no longer references are released (`AssetLibrary.Trim`, `MeshBuilder.TrimMaterials`).
5. **Smoother input** — distinct-frame counting for the FPS readout, smooth dolly integrated per frame.
6. **Web editor** — bounded mesh/texture caches with disposal of evicted GPU resources, two concurrent decodes
   at most (requests wait with cancellation, 503 after 30 s), Brotli/Gzip responses (asset list 1.06 MB → 186 KB),
   cache headers on immutable mesh/texture responses, debounced search.
