using System.Collections.ObjectModel;

namespace BetterHwMonitor;

/// <summary>
/// Holds every sensor and its history. All sensors share one timestamp list,
/// so History[i] of every sensor belongs to Times[i]. UI thread only.
/// </summary>
public sealed class DataStore
{
    private readonly Dictionary<string, SensorRecord> _byId = new();
    private long _tick;

    /// <summary>Local time that Times are measured from (the oldest loaded history, or app start).</summary>
    public DateTime StartTime { get; private set; } = DateTime.Now;

    /// <summary>Seconds since StartTime for each sample.</summary>
    public List<double> Times { get; } = new();

    public ObservableCollection<SensorRecord> Sensors { get; } = new();

    /// <summary>How much history to keep in memory.</summary>
    public double KeepSeconds { get; set; } = DefaultKeepSeconds;

    /// <summary>History kept in memory and on disk: 3 days.</summary>
    public const double DefaultKeepSeconds = 3 * 24 * 3600;

    public SensorRecord? Find(string id) => _byId.TryGetValue(id, out var r) ? r : null;

    /// <summary>
    /// Fills the store with history from the log. Call once, before the first Append.
    /// A blank row is added at the end so the graphs show a gap for the time the app was closed.
    /// </summary>
    public void Import(LoadedHistory history)
    {
        if (history.Times.Count == 0 || Times.Count > 0) return;

        double t0 = history.Times[0];
        StartTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(t0 * 1000)).LocalDateTime;
        Times.Capacity = history.Times.Count + 1024;
        foreach (var t in history.Times) Times.Add(t - t0);

        for (int i = 0; i < history.Sensors.Count; i++)
        {
            var s = history.Sensors[i];
            var rec = new SensorRecord(s.Id, s.Hardware, s.HardwareType, s.Name, s.Type);
            var column = history.Columns[i];
            rec.History.Capacity = column.Count + 1024;
            float scale = (float)rec.Scale;
            foreach (var v in column) rec.History.Add(float.IsNaN(v) ? float.NaN : v * scale);
            history.Columns[i] = new List<float>(); // free the loaded copy as we go
            _byId[s.Id] = rec;
            Sensors.Add(rec);
        }

        // Gap marker between the old session and this one
        Times.Add(Times[^1] + 0.001);
        foreach (var rec in Sensors) rec.History.Add(float.NaN);
    }

    public void Append(double t, IReadOnlyList<SensorSample> samples)
    {
        _tick++;
        int existing = Times.Count;
        Times.Add(t);

        foreach (var s in samples)
        {
            if (!_byId.TryGetValue(s.Id, out var rec))
            {
                rec = new SensorRecord(s.Id, s.Hardware, s.HardwareType, s.Name, s.Type);
                for (int i = 0; i < existing; i++) rec.History.Add(float.NaN);
                _byId[s.Id] = rec;
                Sensors.Add(rec);
            }

            if (rec.LastTick == _tick) continue; // duplicate id in one read
            rec.LastTick = _tick;
            rec.Push(s.Value);
        }

        // Sensors that did not report this time get a gap.
        foreach (var rec in Sensors)
        {
            if (rec.LastTick == _tick) continue;
            rec.LastTick = _tick;
            rec.PushMissing();
        }

        Trim(t);
    }

    private void Trim(double now)
    {
        double cutoff = now - KeepSeconds;
        int remove = 0;
        while (remove < Times.Count && Times[remove] < cutoff) remove++;
        if (remove < 256) return; // remove in batches, not every tick

        Times.RemoveRange(0, remove);
        foreach (var rec in Sensors)
            rec.History.RemoveRange(0, Math.Min(remove, rec.History.Count));
    }
}
