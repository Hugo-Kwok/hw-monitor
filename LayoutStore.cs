using System.IO;
using System.Text.Json;

namespace BetterHwMonitor;

public sealed class SeriesLayout
{
    public string SensorId { get; set; } = "";
    public string Color { get; set; } = "#3B8EEA";
    public bool RightAxis { get; set; }

    /// <summary>Custom name for this graph; null = original sensor name.</summary>
    public string? Name { get; set; }
}

public sealed class GraphLayout
{
    public string Title { get; set; } = "";
    public double Height { get; set; } = GraphCard.DefaultHeight;
    public double? LeftMin { get; set; }
    public double? LeftMax { get; set; }
    public double? RightMin { get; set; }
    public double? RightMax { get; set; }
    public List<SeriesLayout> Series { get; set; } = new();
}

public sealed class LayoutFile
{
    public double WindowSeconds { get; set; } = 300;
    public int IntervalMs { get; set; } = 1000;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = double.NaN;
    public double Height { get; set; } = double.NaN;
    public bool Maximized { get; set; }
    public List<GraphLayout> Graphs { get; set; } = new();

    /// <summary>True when the whole sensor list is hidden to give the graphs more room.</summary>
    public bool SensorsHidden { get; set; }
    public double SensorPanelWidth { get; set; } = 520;

    /// <summary>Sensor categories (hardware names) that are collapsed in the list.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    /// <summary>Legend width shared by all graphs.</summary>
    public double LegendWidth { get; set; } = GraphCard.DefaultLegendWidth;
}

/// <summary>Reads and writes %AppData%\BetterHwMonitor\layout.json.</summary>
public static class LayoutStore
{
    private static readonly string LayoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterHwMonitor", "layout.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static LayoutFile? Load()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return null;
            return JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(LayoutPath), Options);
        }
        catch
        {
            return null; // a broken file should never stop the app from starting
        }
    }

    public static void Save(LayoutFile layout)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            var temp = LayoutPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(layout, Options));
            File.Move(temp, LayoutPath, overwrite: true);
        }
        catch
        {
            // saving is best-effort
        }
    }
}
