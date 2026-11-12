using System.IO;
using System.Runtime.InteropServices;

namespace BetterHwMonitor;

/// <summary>A sensor as stored in the history log.</summary>
public sealed record LoggedSensor(string Id, string Hardware, string HardwareType, string Name, string Type);

/// <summary>History read back from disk: one column of raw values per sensor, aligned with Times.</summary>
public sealed class LoadedHistory
{
    public List<LoggedSensor> Sensors { get; } = new();
    /// <summary>UTC time of each row, as Unix seconds.</summary>
    public List<double> Times { get; } = new();
    /// <summary>Columns[i][row] = raw value of Sensors[i] (NaN = no reading).</summary>
    public List<List<float>> Columns { get; } = new();
}

/// <summary>
/// Records every sensor reading to %AppData%\BetterHwMonitor\history so graphs survive a restart.
/// Data is split into one file per hour; files older than the keep time are deleted.
/// File format (binary, little-endian):
///   header:  "BHWL" + int version
///   record 1 (new sensor): int index, string id, hardware, hardwareType, name, type
///   record 2 (sample row): double unixSeconds, int count, count x float (raw value per sensor index, NaN = none)
/// Each file defines its own sensor indices, so every file can be read on its own.
/// </summary>
public sealed class HistoryLog : IDisposable
{
    private const int Version = 1;
    private const byte RecSensor = 1, RecSample = 2;
    private static readonly byte[] Magic = "BHWL"u8.ToArray();

    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterHwMonitor", "history");

    private readonly double _keepSeconds;
    private readonly object _lock = new();
    private BinaryWriter? _writer;
    private DateTime _segmentHour;                       // UTC hour of the open file
    private readonly Dictionary<string, int> _index = new(); // sensor id -> index in the open file
    private float[] _row = Array.Empty<float>();
    private DateTime _lastFlush = DateTime.MinValue;
    private bool _disposed;

    public HistoryLog(double keepSeconds) => _keepSeconds = keepSeconds;

    // ---------- Writing ----------

    /// <summary>Appends one reading of all sensors. Safe to call from the polling thread.</summary>
    public void Write(DateTime utcNow, IReadOnlyList<SensorSample> samples)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                var hour = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc);
                if (_writer == null || hour != _segmentHour) OpenSegment(hour);
                var w = _writer!;

                // Define sensors this file hasn't seen yet
                foreach (var s in samples)
                {
                    if (_index.ContainsKey(s.Id)) continue;
                    int idx = _index.Count;
                    _index[s.Id] = idx;
                    w.Write(RecSensor);
                    w.Write(idx);
                    w.Write(s.Id); w.Write(s.Hardware); w.Write(s.HardwareType); w.Write(s.Name); w.Write(s.Type);
                }

                if (_row.Length != _index.Count) _row = new float[_index.Count];
                Array.Fill(_row, float.NaN);
                foreach (var s in samples)
                    _row[_index[s.Id]] = s.Value ?? float.NaN;

                w.Write(RecSample);
                w.Write(new DateTimeOffset(utcNow).ToUnixTimeMilliseconds() / 1000.0);
                w.Write(_row.Length);
                w.Write(MemoryMarshal.AsBytes(_row.AsSpan()));

                // Flush every few seconds: at most a few seconds are lost if the PC crashes
                if ((utcNow - _lastFlush).TotalSeconds >= 5)
                {
                    w.Flush();
                    _lastFlush = utcNow;
                }
            }
            catch
            {
                // Logging is best-effort; never stop monitoring because of a disk problem
                CloseWriter();
            }
        }
    }

    private void OpenSegment(DateTime hourUtc)
    {
        CloseWriter();
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"{hourUtc:yyyy-MM-dd_HH}.bhwl");

        // If the app restarts within the same hour, start a new numbered file instead of appending,
        // so each file stays self-contained.
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(Folder, $"{hourUtc:yyyy-MM-dd_HH}_{n}.bhwl");

        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
        _writer = new BinaryWriter(fs);
        _writer.Write(Magic);
        _writer.Write(Version);
        _segmentHour = hourUtc;
        _index.Clear();
        DeleteOldFiles();
    }

    private void DeleteOldFiles()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_keepSeconds).AddHours(-1);
        foreach (var file in Directory.EnumerateFiles(Folder, "*.bhwl"))
        {
            if (FileHour(file) is DateTime h && h < cutoff)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private static DateTime? FileHour(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length < 13) return null;
        return DateTime.TryParseExact(name[..13], "yyyy-MM-dd_HH", null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var h) ? h : null;
    }

    private void CloseWriter()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    // ---------- Reading ----------

    /// <summary>Reads every file from the last keepSeconds. A file cut short by a crash is read up to the break.</summary>
    public static LoadedHistory Load(double keepSeconds)
    {
        var result = new LoadedHistory();
        if (!Directory.Exists(Folder)) return result;

        double cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - keepSeconds;
        var global = new Dictionary<string, int>();

        var files = Directory.EnumerateFiles(Folder, "*.bhwl")
            .Select(f => (File: f, Hour: FileHour(f)))
            .Where(f => f.Hour != null && f.Hour.Value >= DateTime.UtcNow.AddSeconds(-keepSeconds).AddHours(-1))
            .OrderBy(f => f.Hour).ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.File);

        foreach (var file in files)
        {
            try { ReadFile(file, cutoff, result, global); }
            catch { /* skip unreadable files */ }
        }

        // Pad every column to the full length
        foreach (var col in result.Columns)
            while (col.Count < result.Times.Count) col.Add(float.NaN);
        return result;
    }

    private static void ReadFile(string path, double cutoff, LoadedHistory result, Dictionary<string, int> global)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var r = new BinaryReader(fs);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadInt32() != Version) return;

        var localToGlobal = new List<int>();
        float[] buffer = Array.Empty<float>();
        try
        {
            while (fs.Position < fs.Length)
            {
                byte kind = r.ReadByte();
                if (kind == RecSensor)
                {
                    int idx = r.ReadInt32();
                    var s = new LoggedSensor(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
                    if (!global.TryGetValue(s.Id, out int g))
                    {
                        g = result.Sensors.Count;
                        global[s.Id] = g;
                        result.Sensors.Add(s);
                        result.Columns.Add(new List<float>());
                    }
                    while (localToGlobal.Count <= idx) localToGlobal.Add(-1);
                    localToGlobal[idx] = g;
                }
                else if (kind == RecSample)
                {
                    double t = r.ReadDouble();
                    int count = r.ReadInt32();
                    if (count < 0 || count > 1_000_000) return; // corrupt
                    if (buffer.Length < count) buffer = new float[count];
                    var bytes = MemoryMarshal.AsBytes(buffer.AsSpan(0, count));
                    fs.ReadExactly(bytes); // throws EndOfStreamException if cut short by a crash
                    if (t < cutoff) continue;
                    if (result.Times.Count > 0 && t <= result.Times[^1]) continue; // keep time strictly increasing

                    int row = result.Times.Count;
                    result.Times.Add(t);
                    for (int k = 0; k < count && k < localToGlobal.Count; k++)
                    {
                        int g = localToGlobal[k];
                        if (g < 0) continue;
                        var col = result.Columns[g];
                        while (col.Count < row) col.Add(float.NaN);
                        col.Add(buffer[k]);
                    }
                }
                else return; // unknown record: stop reading this file
            }
        }
        catch (EndOfStreamException)
        {
            // last record was cut short by a crash; everything before it is kept
        }
    }
}
