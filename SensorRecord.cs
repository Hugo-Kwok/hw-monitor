using System.ComponentModel;
using System.Globalization;

namespace BetterHwMonitor;

/// <summary>Unit, display decimals and scale factor per LibreHardwareMonitor sensor type.</summary>
public static class UnitInfo
{
    public static (string Unit, int Decimals, double Scale) For(string sensorType) => sensorType switch
    {
        "Temperature" => ("°C", 1, 1),
        "Voltage" => ("V", 3, 1),
        "Current" => ("A", 2, 1),
        "Power" => ("W", 1, 1),
        "Clock" => ("MHz", 0, 1),
        "Load" => ("%", 1, 1),
        "Frequency" => ("Hz", 0, 1),
        "Fan" => ("RPM", 0, 1),
        "Flow" => ("L/h", 1, 1),
        "Control" => ("%", 1, 1),
        "Level" => ("%", 1, 1),
        "Factor" => ("", 3, 1),
        "Data" => ("GB", 2, 1),
        "SmallData" => ("MB", 0, 1),
        "Throughput" => ("MB/s", 2, 1.0 / (1024 * 1024)),
        "TimeSpan" => ("s", 0, 1),
        "Energy" => ("mWh", 0, 1),
        "Noise" => ("dBA", 1, 1),
        _ => ("", 2, 1),
    };
}

/// <summary>A sensor plus its full recorded history (aligned with DataStore.Times).</summary>
public sealed class SensorRecord : INotifyPropertyChanged
{
    public SensorRecord(string id, string hardware, string hardwareType, string name, string type)
    {
        Id = id;
        Hardware = hardware;
        HardwareType = hardwareType;
        Name = name;
        Type = type;
        var info = UnitInfo.For(type);
        Unit = info.Unit;
        Decimals = info.Decimals;
        Scale = info.Scale;
    }

    public string Id { get; }
    public string Hardware { get; }
    public string HardwareType { get; }
    public string Name { get; }
    public string Type { get; }
    public string Unit { get; }
    public int Decimals { get; }
    public double Scale { get; }

    public string DisplayName => $"{Name} ({Hardware})";

    /// <summary>One value per DataStore.Times entry; NaN = no reading.</summary>
    public List<float> History { get; } = new();

    public double? Current { get; private set; }
    public double? Min { get; private set; }
    public double? Max { get; private set; }

    public string ValueText => Format(Current);
    public string MinText => Format(Min);
    public string MaxText => Format(Max);

    internal long LastTick;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Format(double? v)
    {
        if (v is not double d || double.IsNaN(d)) return "-";
        var text = d.ToString("F" + Decimals, CultureInfo.CurrentCulture);
        return Unit.Length > 0 ? text + " " + Unit : text;
    }

    internal void Push(float? raw)
    {
        double? v = raw.HasValue && !float.IsNaN(raw.Value) ? raw.Value * Scale : null;
        History.Add(v.HasValue ? (float)v.Value : float.NaN);
        SetCurrent(v);
    }

    internal void PushMissing()
    {
        History.Add(float.NaN);
        SetCurrent(null);
    }

    private void SetCurrent(double? v)
    {
        var oldText = ValueText;
        Current = v;
        if (ValueText != oldText) Raise(nameof(ValueText));

        if (v is double d)
        {
            if (Min is null || d < Min) { Min = d; Raise(nameof(MinText)); }
            if (Max is null || d > Max) { Max = d; Raise(nameof(MaxText)); }
        }
    }

    public void ResetMinMax()
    {
        Min = Current;
        Max = Current;
        Raise(nameof(MinText));
        Raise(nameof(MaxText));
    }

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
