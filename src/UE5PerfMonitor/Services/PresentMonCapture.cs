using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace UE5PerfMonitor.Services;

/// <summary>Optional black-box frame capture using the official PresentMon console executable.</summary>
public sealed class PresentMonCapture : IDisposable
{
    private Process? _process;
    private long _position;
    private string _partialLine = "";
    private string[]? _headers;
    private readonly Queue<(double FrameMs, double? GpuBusyMs)> _frames = new();
    private readonly List<double> _newFrameTimes = new();
    public string? CsvPath { get; private set; }

    public void Start(string executable, int pid, string outputCsv)
    {
        Stop();
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 PresentMon 程序。", executable);
        CsvPath = outputCsv;
        _position = 0; _partialLine = ""; _headers = null; _frames.Clear(); _newFrameTimes.Clear();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--process_id"); start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--output_file"); start.ArgumentList.Add(outputCsv);
        start.ArgumentList.Add("--v2_metrics"); start.ArgumentList.Add("--qpc_time_ms");
        start.ArgumentList.Add("--no_console_stats"); start.ArgumentList.Add("--terminate_on_proc_exit");
        _process = Process.Start(start) ?? throw new InvalidOperationException("PresentMon 启动失败。");
    }

    public (double? Fps, double? FrameTimeMs, double? GpuBusyPercent, int HitchCount, double? WorstFrameTimeMs) Poll()
    {
        _newFrameTimes.Clear();
        if (CsvPath is null || !File.Exists(CsvPath)) return (null, null, null, 0, null);
        try
        {
            using var stream = new FileStream(CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position) { _position = 0; _partialLine = ""; _headers = null; }
            stream.Position = _position;
            var buffer = new byte[64 * 1024];
            using var memory = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) memory.Write(buffer, 0, read);
            _position = stream.Position;
            var text = _partialLine + Encoding.UTF8.GetString(memory.ToArray());
            var lines = text.Split('\n');
            _partialLine = lines[^1];
            foreach (var raw in lines.Take(lines.Length - 1)) ProcessLine(raw.TrimEnd('\r'));
        }
        catch (IOException) { return CurrentMetrics(); }
        catch (UnauthorizedAccessException) { return CurrentMetrics(); }
        return CurrentMetrics();
    }

    private void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var fields = SplitCsv(line);
        if (_headers is null)
        {
            if (fields.Any(x => x.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)))
                _headers = fields.Select(x => x.Trim().Trim('\uFEFF')).ToArray();
            return;
        }
        var presentIndex = Array.FindIndex(_headers, x => x.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
        var gpuIndex = Array.FindIndex(_headers, x => x.Equals("MsGPUBusy", StringComparison.OrdinalIgnoreCase));
        if (presentIndex < 0 || presentIndex >= fields.Length) return;
        if (!double.TryParse(fields[presentIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameMs) || frameMs <= 0 || frameMs > 10000) return;
        double? gpuMs = gpuIndex >= 0 && gpuIndex < fields.Length && double.TryParse(fields[gpuIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedGpu) ? parsedGpu : null;
        _frames.Enqueue((frameMs, gpuMs));
        _newFrameTimes.Add(frameMs);
        while (_frames.Count > 120) _frames.Dequeue();
    }

    private (double? Fps, double? FrameTimeMs, double? GpuBusyPercent, int HitchCount, double? WorstFrameTimeMs) CurrentMetrics()
    {
        var hitches = _newFrameTimes.Where(x => x >= 50).ToArray();
        if (_frames.Count == 0) return (null, null, null, hitches.Length, _newFrameTimes.Count == 0 ? null : _newFrameTimes.Max());
        var frameTime = _frames.Average(x => x.FrameMs);
        var gpuRows = _frames.Where(x => x.GpuBusyMs.HasValue).ToArray();
        double? gpu = gpuRows.Length == 0 ? null : Math.Clamp(gpuRows.Average(x => x.GpuBusyMs!.Value) / frameTime * 100, 0, 100);
        return (1000d / frameTime, frameTime, gpu, hitches.Length, _newFrameTimes.Count == 0 ? null : _newFrameTimes.Max());
    }

    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>(); var current = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    public void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
    }
    public void Dispose() => Stop();
}
