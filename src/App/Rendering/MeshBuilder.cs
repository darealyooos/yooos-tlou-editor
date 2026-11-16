using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using FactionsReborn.WorldEditor.Core.Assets;
using FactionsReborn.WorldEditor.Core.Pak;
using FactionsReborn.WorldEditor.Core.Scene;
using N = System.Numerics;

namespace FactionsReborn.WorldEditor.Rendering;

/// <summary>Turns decoded paks into frozen WPF 3D models. All output is frozen, so it can be built off the UI thread.</summary>
public static class MeshBuilder
{
    private static readonly ConcurrentDictionary<string, Material> MaterialCache = new();
    private static readonly Material Untextured = Freeze(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(150, 150, 145))));

    /// <summary>
    /// LOD0 of a prototype (or of an actor's first prototype) in its local space, one GeometryModel3D per material.
    /// <paramref name="cacheMaterials"/> false = materials are not kept in the shared cache (previews, thumbnails),
    /// so browsing thousands of assets does not accumulate textures. <paramref name="textured"/> false = plain material.
    /// </summary>
    public static Model3DGroup BuildPrototype(LoadedPak pak, int? prototype, bool cacheMaterials = true, bool textured = true)
    {
        var geo = pak.Geometry;
        int[] subs = prototype is int p ? geo.Prototypes[p].Lod0Submeshes : GeometryReader.ActorLod0(geo);
        var group = new Model3DGroup();
        foreach (var byMat in subs.GroupBy(s => geo.Submeshes[s].MaterialIndex))
        {
            var batch = new Batch();
            foreach (int s in byMat) batch.Add(geo.Submeshes[s], N.Matrix4x4.Identity);
            if (batch.Empty) continue;
            group.Children.Add(batch.ToModel(textured ? MaterialFor(pak, byMat.Key, cacheMaterials) : Untextured));
        }
        group.Freeze();
        return group;
    }

    /// <summary>All placed instances of a level pak, merged per material and split into vegetation / the rest.</summary>
    public static (Model3DGroup Solid, Model3DGroup Vegetation, int Triangles) BuildLevel(LoadedPak pak, IProgress<string>? progress = null)
    {
        var geo = pak.Geometry;
        var solid = new Dictionary<int, Batch>();
        var veg = new Dictionary<int, Batch>();
        var isVeg = geo.Prototypes.Select(p => AssetIndex.GuessCategory(p.Name) == SceneCategories.Vegetation).ToArray();
        int n = 0, triangles = 0;
        foreach (var inst in geo.Instances)
        {
            if (++n % 200 == 0) progress?.Report($"Building map geometry {n}/{geo.Instances.Count}…");
            var target = isVeg[inst.Prototype] ? veg : solid;
            foreach (int s in geo.Prototypes[inst.Prototype].Lod0Submeshes)
            {
                var mesh = geo.Submeshes[s];
                if (!target.TryGetValue(mesh.MaterialIndex, out var b)) target[mesh.MaterialIndex] = b = new Batch();
                b.Add(mesh, inst.Matrix);
                triangles += mesh.Indices.Length / 3;
            }
        }
        Model3DGroup Make(Dictionary<int, Batch> d)
        {
            var g = new Model3DGroup();
            foreach (var (mat, b) in d) if (!b.Empty) g.Children.Add(b.ToModel(MaterialFor(pak, mat)));
            g.Freeze();
            return g;
        }
        return (Make(solid), Make(veg), triangles);
    }

    /// <summary>Drops cached materials of paks that are no longer used (keys are AssetRef.Key).</summary>
    public static int TrimMaterials(ISet<string> keepPakKeys)
    {
        int removed = 0;
        foreach (var key in MaterialCache.Keys)
            if (!keepPakKeys.Contains(key[..key.IndexOf('|')]) && MaterialCache.TryRemove(key, out _)) removed++;
        return removed;
    }

    public static int CachedMaterials => MaterialCache.Count;

    private static Material MaterialFor(LoadedPak pak, int materialIndex, bool cache = true)
    {
        var tex = pak.Geometry.Materials[materialIndex].BaseColorTexture;
        if (tex is null || !pak.Textures.TryGetValue(tex, out var image)) return Untextured;
        var key = pak.Ref.Key + "|" + tex;
        if (!cache) return MaterialCache.TryGetValue(key, out var shared) ? shared : CreateMaterial(image);
        return MaterialCache.GetOrAdd(key, _ => CreateMaterial(image));
    }

    private static Material CreateMaterial(TextureImage image)
    {
        {
            var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
            bmp.Freeze();
            var brush = new ImageBrush(bmp)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
            };
            return Freeze(new DiffuseMaterial(brush));
        }
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private sealed class Batch
    {
        private readonly List<Point3D> _positions = new();
        private readonly List<Point> _uvs = new();
        private readonly List<int> _indices = new();
        public bool Empty => _indices.Count == 0;

        public void Add(MeshData mesh, N.Matrix4x4 world)
        {
            if (mesh.Indices.Length == 0) return;
            int baseIndex = _positions.Count;
            bool identity = world.IsIdentity;
            for (int i = 0; i < mesh.Positions.Length; i++)
            {
                var p = identity ? mesh.Positions[i] : N.Vector3.Transform(mesh.Positions[i], world);
                _positions.Add(new Point3D(p.X, p.Y, p.Z));
                var uv = mesh.TexCoords is { } t ? t[i] : default;
                _uvs.Add(new Point(uv.X, uv.Y));
            }
            // A mirrored instance (negative determinant) flips winding; keep faces pointing outwards.
            bool flip = !identity && world.GetDeterminant() < 0;
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                _indices.Add(baseIndex + mesh.Indices[i]);
                _indices.Add(baseIndex + mesh.Indices[flip ? i + 2 : i + 1]);
                _indices.Add(baseIndex + mesh.Indices[flip ? i + 1 : i + 2]);
            }
        }

        public GeometryModel3D ToModel(Material material)
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(_positions),
                TextureCoordinates = new PointCollection(_uvs),
                TriangleIndices = new Int32Collection(_indices),
            };
            mesh.Freeze();
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
            model.Freeze();
            return model;
        }
    }
}
