using LibreHardwareMonitor.Hardware;

namespace BetterHwMonitor;

/// <summary>One reading of one sensor, produced on the background polling thread.</summary>
public readonly record struct SensorSample(
    string Id, string Hardware, string HardwareType, string Name, string Type, float? Value);

/// <summary>Thin wrapper around LibreHardwareMonitor's Computer object.</summary>
public sealed class SensorReader : IDisposable
{
    private readonly Computer _computer;

    public SensorReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsPsuEnabled = true,
        };
        _computer.Open();
    }

    public List<SensorSample> Read()
    {
        var list = new List<SensorSample>(512);
        foreach (var hw in _computer.Hardware)
            Visit(hw, list);
        return list;
    }

    private static void Visit(IHardware hw, List<SensorSample> list)
    {
        try { hw.Update(); } catch { /* one broken device should not stop the rest */ }

        foreach (var s in hw.Sensors)
        {
            list.Add(new SensorSample(
                s.Identifier.ToString(),
                hw.Name,
                hw.HardwareType.ToString(),
                s.Name,
                s.SensorType.ToString(),
                s.Value));
        }

        foreach (var sub in hw.SubHardware)
            Visit(sub, list);
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
    }
}
