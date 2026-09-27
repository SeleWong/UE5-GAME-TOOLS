namespace UE5PerfMonitor.Models;

public sealed record MetricSample(
    DateTime Time,
    double? GameCpuPercent,
    double SystemCpuPercent,
    double GameMemoryMb,
    double GameMemoryPercent,
    double SystemMemoryPercent,
    double? Fps = null,
    double? FrameTimeMs = null,
    double? GpuBusyPercent = null,
    int HitchCount = 0,
    double? WorstFrameTimeMs = null);

public sealed record AlertEvent(DateTime Time, string Severity, string Title, string Detail);
