using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactionsReborn.WorldEditor.Core.Scene;

/// <summary>A pak inside one of the game's psarcs, e.g. ("world-seattle-arrival.psarc", "actor97/bucket-rusty-a.pak").</summary>
public sealed record AssetRef(string Archive, string Path)
{
    [JsonIgnore] public string Key => Archive + "|" + Path;
    public override string ToString() => $"{Path} ({Archive})";
}

public static class SceneCategories
{
    public const string Buildings = "Buildings";
    public const string Props = "Props";
    public const string Vegetation = "Vegetation";
    public const string Objects = "Objects";
    public const string Map = "Map";
    public static readonly string[] Placeable = [Buildings, Props, Vegetation, Objects];
}

public enum Team { WLF, Seraphites }

public sealed class SceneObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Category { get; set; } = SceneCategories.Props;
    public required AssetRef Asset { get; set; }
    /// <summary>Prototype index when the asset is a level pak prototype (a native building); null for an actor pak.</summary>
    public int? Prototype { get; set; }
    /// <summary>Prototype name, checked on load so a changed game build cannot silently swap meshes.</summary>
    public string? PrototypeName { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 RotationDegrees { get; set; }
    public Vector3 Scale { get; set; } = Vector3.One;

    /// <summary>Engine convention: row vectors, translation in M41..M43, metres, Y up (same layout as level instances).</summary>
    public Matrix4x4 EngineMatrix() => EngineTransform.Compose(Position, RotationDegrees, Scale);
}

public sealed class SpawnPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Team Team { get; set; }
    public Vector3 Position { get; set; }
    public float YawDegrees { get; set; }
}

public sealed class MapBoundary
{
    /// <summary>Polygon on the XZ plane, in order.</summary>
    public List<Vector2> Points { get; set; } = [];
    public float MinY { get; set; } = -10;
    public float MaxY { get; set; } = 50;

    public bool Contains(Vector3 p)
    {
        if (Points.Count < 3 || p.Y < MinY || p.Y > MaxY) return false;
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var a = Points[i]; var b = Points[j];
            if ((a.Y > p.Z) != (b.Y > p.Z) && p.X < (b.X - a.X) * (p.Z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}

/// <summary>Editor project (.frscene). This is the editor's own save format, not a game file.</summary>
public sealed class SceneDocument
{
    public const string FormatId = "FactionsReborn.WorldEditor.Scene";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Untitled arena";
    public List<AssetRef> Maps { get; set; } = [];
    public List<SceneObject> Objects { get; set; } = [];
    public List<SpawnPoint> Spawns { get; set; } = [];
    public MapBoundary Boundary { get; set; } = new();
    /// <summary>Last editor camera, so a reopened scene looks the way it was left.</summary>
    public EditorCamera? Camera { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new Vector3Converter(), new Vector2Converter() },
    };

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static SceneDocument Load(string path)
    {
        var doc = JsonSerializer.Deserialize<SceneDocument>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty scene file.");
        if (doc.Format != FormatId) throw new InvalidDataException($"Not a Factions Reborn scene ({doc.Format}).");
        if (doc.Version > CurrentVersion) throw new InvalidDataException($"Scene version {doc.Version} is newer than this editor ({CurrentVersion}).");
        return doc;
    }
}

public sealed class EditorCamera
{
    public Vector3 Position { get; set; }
    public float YawDegrees { get; set; }
    public float PitchDegrees { get; set; }
}

public static class EngineTransform
{
    /// <summary>Scale, then rotate X, Y, Z (degrees), then translate — row-vector order.</summary>
    public static Matrix4x4 Compose(Vector3 position, Vector3 rotationDegrees, Vector3 scale)
    {
        const float d2r = MathF.PI / 180f;
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationX(rotationDegrees.X * d2r)
            * Matrix4x4.CreateRotationY(rotationDegrees.Y * d2r)
            * Matrix4x4.CreateRotationZ(rotationDegrees.Z * d2r)
            * Matrix4x4.CreateTranslation(position);
    }
}

internal sealed class Vector3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        var a = JsonSerializer.Deserialize<float[]>(ref reader, o) ?? [];
        return a.Length == 3 ? new Vector3(a[0], a[1], a[2]) : throw new JsonException("Expected [x, y, z].");
    }
    public override void Write(Utf8JsonWriter w, Vector3 v, JsonSerializerOptions o)
    {
        w.WriteStartArray(); w.WriteNumberValue(v.X); w.WriteNumberValue(v.Y); w.WriteNumberValue(v.Z); w.WriteEndArray();
    }
}

internal sealed class Vector2Converter : JsonConverter<Vector2>
{
    public override Vector2 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        var a = JsonSerializer.Deserialize<float[]>(ref reader, o) ?? [];
        return a.Length == 2 ? new Vector2(a[0], a[1]) : throw new JsonException("Expected [x, z].");
    }
    public override void Write(Utf8JsonWriter w, Vector2 v, JsonSerializerOptions o)
    {
        w.WriteStartArray(); w.WriteNumberValue(v.X); w.WriteNumberValue(v.Y); w.WriteEndArray();
    }
}
