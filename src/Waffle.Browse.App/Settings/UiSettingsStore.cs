using System.IO;
using System.Text.Json;

namespace Waffle.Browse.App.Settings;

public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string filePath;

    public UiSettingsStore(string filePath)
    {
        this.filePath = filePath;
    }

    public UiSettings Load()
    {
        if (!File.Exists(filePath))
        {
            return new UiSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(filePath), JsonOptions)
                ?? new UiSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UiSettings();
        }
    }

    public void Save(UiSettings settings)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
