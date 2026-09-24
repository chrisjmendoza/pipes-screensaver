using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pipes;

public enum JointStyle
{
    /// <summary>Big ball at every bend, like the original.</summary>
    Classic,
    /// <summary>Pipe-width sphere at bends, which reads as a smooth rounded elbow.</summary>
    Smooth,
    /// <summary>Random per bend.</summary>
    Mixed,
}

public enum Finish
{
    /// <summary>Glossy plastic, closest to the 90s look.</summary>
    Plastic,
    /// <summary>Metallic with coloured reflections.</summary>
    Metallic,
}

public sealed class PipesSettings
{
    /// <summary>Pipes growing at the same time.</summary>
    public int ConcurrentPipes { get; set; } = 3;

    /// <summary>Pipes drawn before the scene fades and restarts.</summary>
    public int PipesPerScene { get; set; } = 18;

    /// <summary>Grid cells per second, per pipe.</summary>
    public float Speed { get; set; } = 9f;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JointStyle Joints { get; set; } = JointStyle.Classic;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Finish Finish { get; set; } = Finish.Plastic;

    /// <summary>Slow orbit of the camera around the scene.</summary>
    public bool CameraDrift { get; set; } = true;

    /// <summary>MSAA samples (0, 2, 4, 8).</summary>
    public int Antialiasing { get; set; } = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipesScreensaver", "settings.json");

    public static PipesSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<PipesSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new()).Clamped();
        }
        catch (JsonException) { /* fall back to defaults */ }
        catch (IOException) { }
        return new PipesSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Clamped(), JsonOptions));
    }

    public PipesSettings Clamped()
    {
        ConcurrentPipes = Math.Clamp(ConcurrentPipes, 1, 10);
        PipesPerScene = Math.Clamp(PipesPerScene, ConcurrentPipes, 100);
        Speed = Math.Clamp(Speed, 1f, 40f);
        Antialiasing = Antialiasing switch { <= 0 => 0, <= 2 => 2, <= 4 => 4, _ => 8 };
        return this;
    }
}
