using System.IO;
using System.Text.Json;

namespace Kadraj.Effects;

/// <summary>Everything the viewer sees on top of the raw phone image. Immutable: change with <c>with</c>.</summary>
public sealed record EffectSettings
{
    public string Filter { get; init; } = "normal";
    public double Brightness { get; init; }  // -1..1
    public double Contrast { get; init; }    // -1..1
    public double Saturation { get; init; }  // -1..1

    public string Frame { get; init; } = "none";
    public string? CustomFramePath { get; init; }
    public string Accent { get; init; } = "#FF5A36";

    public bool ShowNameTag { get; init; }
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";

    public string Background { get; init; } = "none";  // none, blur, studio, sunset, image
    public double BlurStrength { get; init; } = 0.6;    // 0..1
    public string? BackgroundImagePath { get; init; }

    public bool Mirror { get; init; }
    public bool Fill { get; init; } = true; // crop to fill the frame instead of adding black bars

    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kadraj");
    static string FilePath => Path.Combine(Folder, "effects.json");
    public static string CustomFrameCopy => Path.Combine(Folder, "custom-frame.png");
    public static string BackgroundCopy(string extension) => Path.Combine(Folder, "background" + extension.ToLowerInvariant());

    public static EffectSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<EffectSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting the user; settings just won't persist.
        }
    }
}
