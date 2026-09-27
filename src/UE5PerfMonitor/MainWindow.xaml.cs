using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using UE5PerfMonitor.Controls;
using UE5PerfMonitor.Models;
using UE5PerfMonitor.Services;

namespace UE5PerfMonitor;

public partial class MainWindow : Window
{
    private const int MaxChartPoints = 120;
    private readonly ObservableCollection<ProcessChoice> _processes = new();
    private readonly ObservableCollection<string> _alerts = new();
    private readonly List<MetricSample> _samples = new();
    private readonly List<AlertEvent> _alertEvents = new();
    private readonly Dictionary<string, DateTime> _lastAlertAt = new();
    private SystemMetricsCollector _collector = new();
    private readonly PresentMonCapture _presentMon = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Process? _targetProcess;
    private string? _sessionFolder;
    private DateTime _sessionStarted;
    private bool _sessionRunning;
    private bool _memoryGrowthAlertSent;
    private double _memoryThresholdGb = 8;
    private double _systemCpuThreshold = 95;
    private readonly Queue<double> _memoryTrend = new();

    public MainWindow()
    {
        InitializeComponent();
        ProcessCombo.ItemsSource = _processes;
        AlertsList.ItemsSource = _alerts;
        _timer.Tick += SampleTick;
        RefreshProcesses();
        RefreshCharts();
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    private void BrowsePresentMon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 PresentMon 2.x 控制台程序",
            Filter = "PresentMon 程序 (PresentMon*.exe)|PresentMon*.exe|所有可执行文件 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) PresentMonPathBox.Text = dialog.FileName;
    }

    private void RefreshProcesses()
    {
        var selectedPid = (ProcessCombo.SelectedItem as ProcessChoice)?.Pid;
        _processes.Clear();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.HasExited || string.IsNullOrWhiteSpace(p.MainWindowTitle)) { p.Dispose(); continue; }
                var title = p.MainWindowTitle.Trim();
                var name = p.ProcessName;
                _processes.Add(new ProcessChoice(p.Id, $"{title}  ·  {name}.exe  (PID {p.Id})", name));
                p.Dispose();
            }
            catch { p.Dispose(); }
        }
        var selected = selectedPid is null ? null : _processes.FirstOrDefault(x => x.Pid == selectedPid);
        ProcessCombo.SelectedItem = selected ?? _processes.FirstOrDefault(x => x.ProcessName.Contains("Unreal", StringComparison.OrdinalIgnoreCase));
        if (!_sessionRunning) FpsSub.Text = $"发现 {_processes.Count} 个可选游戏窗口进程";
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessCombo.SelectedItem is not ProcessChoice choice)
        {
            MessageBox.Show("请先选择一个正在运行的游戏进程。", "未选择进程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _targetProcess = Process.GetProcessById(choice.Pid);
            if (_targetProcess.HasExited) throw new InvalidOperationException("目标进程已退出。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开所选进程：{ex.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshProcesses();
            return;
        }

        if (!TryThresholds(out _memoryThresholdGb, out _systemCpuThreshold)) return;
        _samples.Clear(); _alertEvents.Clear(); _alerts.Clear(); _lastAlertAt.Clear(); _memoryTrend.Clear();
        _memoryGrowthAlertSent = false;
        _collector = new SystemMetricsCollector();
        FpsOverlay.Visibility = Visibility.Visible;
        _sessionStarted = DateTime.Now;
        _sessionFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UE5PerfMonitor", "Sessions", _sessionStarted.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(_sessionFolder);
        _sessionRunning = true;
        StartButton.IsEnabled = false; ProcessCombo.IsEnabled = false; MemoryThresholdBox.IsEnabled = false; CpuThresholdBox.IsEnabled = false; StopButton.IsEnabled = true;
        SessionStatus.Text = "正在采集"; StatusDot.Fill = new SolidColorBrush(Color.FromRgb(55, 210, 151));
        SessionInfoText.Text = $"目标：{choice.Label}\n采样间隔：1 秒 · 异常快照包含触发前 15 秒数据";
        OutputPathText.Text = $"本次报告：{_sessionFolder}";
        if (!string.IsNullOrWhiteSpace(PresentMonPathBox.Text))
        {
            try
            {
                _presentMon.Start(PresentMonPathBox.Text.Trim(), choice.Pid, Path.Combine(_sessionFolder, "presentmon.csv"));
                FpsOverlay.Visibility = Visibility.Collapsed;
                AddEvent("信息", "帧率采集已启动", "PresentMon 正在采集帧呈现事件。若系统拒绝 ETW 访问，请以管理员身份重启本工具。");
            }
            catch (Exception ex)
            {
                AddEvent("警告", "PresentMon 未启动", ex.Message);
                FpsOverlay.Visibility = Visibility.Visible;
            }
        }
        AddEvent("信息", "评测开始", $"已开始监控 {choice.ProcessName}.exe (PID {choice.Pid})");
        _timer.Start();
        SampleTick(this, EventArgs.Empty);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => FinishSession("用户结束评测");

    private void SampleTick(object? sender, EventArgs e)
    {
        if (!_sessionRunning || _targetProcess is null) return;
        try
        {
            if (_targetProcess.HasExited) { FinishSession("目标进程已退出"); return; }
            var frame = _presentMon.Poll();
            var sample = _collector.Read(_targetProcess, frame.Fps, frame.FrameTimeMs, frame.GpuBusyPercent, frame.HitchCount, frame.WorstFrameTimeMs);
            _samples.Add(sample);
            UpdateCards(sample);
            RefreshCharts();
            CheckAlerts(sample);
            ElapsedText.Text = (DateTime.Now - _sessionStarted).ToString("hh\:mm\:ss");
        }
        catch (Exception ex)
        {
            AddEvent("警告", "采样异常", ex.Message);
        }
    }

    private void UpdateCards(MetricSample s)
    {
        GameMemoryValue.Text = (s.GameMemoryMb / 1024).ToString("0.00", CultureInfo.InvariantCulture);
        SystemCpuValue.Text = s.SystemCpuPercent.ToString("0", CultureInfo.InvariantCulture);
        GameCpuValue.Text = s.GameCpuPercent?.ToString("0", CultureInfo.InvariantCulture) ?? "—";
        FpsValue.Text = s.Fps?.ToString("0", CultureInfo.InvariantCulture) ?? "—";
        FpsSub.Text = $"系统内存 {s.SystemMemoryPercent:0}% · 帧时间 {(s.FrameTimeMs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—")} ms · GPU忙碌 {(s.GpuBusyPercent?.ToString("0", CultureInfo.InvariantCulture) ?? "—")}%";
        try
        {
            if (_targetProcess is { HasExited: false })
            {
                _targetProcess.Refresh();
                GameMemorySub.Text = $"私有内存 {(_targetProcess.PrivateMemorySize64 / 1024d / 1024 / 1024):0.00} GB";
                // Additional process metadata is captured in threshold-triggered JSON snapshots.
            }
        }
        catch { }
    }

    private void CheckAlerts(MetricSample s)
    {
        if (s.SystemCpuPercent >= _systemCpuThreshold)
            RaiseAlert("system-cpu", "严重", $"系统 CPU 达到 {s.SystemCpuPercent:0}%", $"超过设定阈值 {_systemCpuThreshold:0}%；已保存触发前后指标快照。");
        if (s.GameCpuPercent is double gameCpu && gameCpu >= 100)
            RaiseAlert("game-cpu", "警告", $"游戏进程 CPU 达到 {gameCpu:0}%", "进程 CPU 以单逻辑核心为 100% 计；已保存触发前后指标快照。");
        if (s.GameMemoryMb >= _memoryThresholdGb * 1024)
            RaiseAlert("game-memory", "严重", $"游戏工作集超过 {_memoryThresholdGb:0.##} GB", $"当前 {s.GameMemoryMb / 1024:0.00} GB；已保存触发前后指标快照。");
        if (s.SystemMemoryPercent >= 90)
            RaiseAlert("system-memory", "警告", $"系统内存压力 {s.SystemMemoryPercent:0}%", "系统可用物理内存偏低；已保存触发前后指标快照。");
        if (s.HitchCount > 0)
            RaiseAlert("frame-hitch", "警告", $"检测到 {s.HitchCount} 次严重帧间隔", $"采样窗口内帧间隔 ≥50 ms；最慢 {s.WorstFrameTimeMs:0.0} ms。此阈值可视作严重卡顿线索。");
        if (s.Fps is < 30)
            RaiseAlert("low-fps", "观察", $"帧率低于 30 FPS（{s.Fps:0}）", $"平均帧时间约 {s.FrameTimeMs:0.0} ms；保存了前后性能指标快照。");

        _memoryTrend.Enqueue(s.GameMemoryMb);
        while (_memoryTrend.Count > 30) _memoryTrend.Dequeue();
        if (!_memoryGrowthAlertSent && _samples.Count >= 30 && _memoryTrend.Count >= 30)
        {
            var baseline = _samples.Take(10).Average(x => x.GameMemoryMb);
            var recent = _memoryTrend.Average();
            if (baseline > 128 && recent > baseline * 1.25)
            {
                _memoryGrowthAlertSent = true;
                RaiseAlert("memory-growth", "观察", $"内存呈持续增长趋势（+{(recent / baseline - 1) * 100:0}%）", "这是风险提示，不等同于确认内存泄漏；黑盒工具无法直接观察 UE GC 或对象引用。");
            }
        }
    }

    private bool TryThresholds(out double memoryGb, out double cpuPercent)
    {
        var memoryOk = double.TryParse(MemoryThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out memoryGb) && memoryGb > 0;
        var cpuOk = double.TryParse(CpuThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out cpuPercent) && cpuPercent is > 0 and <= 100;
        if (!memoryOk || !cpuOk)
        {
            MessageBox.Show("请填写有效阈值：内存必须大于 0 GB，系统 CPU 必须在 1–100% 之间。", "阈值无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void RaiseAlert(string key, string severity, string title, string detail)
    {
        var now = DateTime.Now;
        if (_lastAlertAt.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(60)) return;
        _lastAlertAt[key] = now;
        var evt = new AlertEvent(now, severity, title, detail);
        _alertEvents.Add(evt);
        AddEvent(severity, title, detail);
        SaveTriggerSnapshot(evt);
    }

    private void SaveTriggerSnapshot(AlertEvent evt)
    {
        if (_sessionFolder is null) return;
        try
        {
            var recent = _samples.Where(x => x.Time >= evt.Time.AddSeconds(-15) && x.Time <= evt.Time.AddSeconds(1)).ToArray();
            object? process = null;
            if (_targetProcess is { HasExited: false })
            {
                _targetProcess.Refresh();
                process = new
                {
                    pid = _targetProcess.Id,
                    name = _targetProcess.ProcessName,
                    windowTitle = Safe(() => _targetProcess.MainWindowTitle),
                    startTime = Safe(() => _targetProcess.StartTime.ToString("O")),
                    threadCount = Safe(() => _targetProcess.Threads.Count),
                    handleCount = Safe(() => _targetProcess.HandleCount),
                    workingSetBytes = Safe(() => _targetProcess.WorkingSet64),
                    privateBytes = Safe(() => _targetProcess.PrivateMemorySize64),
                    totalProcessorTime = Safe(() => _targetProcess.TotalProcessorTime.ToString())
                };
            }
            var file = Path.Combine(_sessionFolder, $"snapshot_{evt.Time:HHmmss}_{Sanitize(evt.Title)}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { alert = evt, process, recentSamples = recent, note = "外部指标快照；不是进程内存转储（dump）。" }, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex) { AddEvent("警告", "快照保存失败", ex.Message); }
    }

    private static object? Safe<T>(Func<T> get) { try { return get(); } catch { return null; } }

    private void AddEvent(string severity, string title, string detail)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  [{severity}]  {title} — {detail}";
        _alerts.Insert(0, line);
        while (_alerts.Count > 100) _alerts.RemoveAt(_alerts.Count - 1);
        AlertCountText.Text = $"{_alertEvents.Count} 条告警";
    }

    private void RefreshCharts()
    {
        var data = _samples.TakeLast(MaxChartPoints).ToArray();
        var cpuMax = Math.Max(100, data.Where(x => x.GameCpuPercent.HasValue).Select(x => x.GameCpuPercent!.Value).DefaultIfEmpty(100).Max() * 1.15);
        CpuChart.Maximum = cpuMax;
        CpuChart.Threshold = double.TryParse(CpuThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpuThreshold) ? cpuThreshold : 95;
        CpuChart.Lines = new[]
        {
            new ChartLine("系统", data.Select(x => (double?)x.SystemCpuPercent).ToArray(), Color.FromRgb(91, 170, 255)),
            new ChartLine("游戏进程", data.Select(x => x.GameCpuPercent).ToArray(), Color.FromRgb(90, 221, 168))
        };
        var memThreshold = double.TryParse(MemoryThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mt) ? mt : 8;
        MemoryChart.Maximum = Math.Max(memThreshold * 1.25, data.Select(x => x.GameMemoryMb / 1024).DefaultIfEmpty(16).Max() * 1.15);
        MemoryChart.Threshold = memThreshold;
        MemoryChart.Lines = new[] { new ChartLine("工作集", data.Select(x => (double?)(x.GameMemoryMb / 1024)).ToArray(), Color.FromRgb(196, 133, 255)) };
        SystemMemoryChart.Threshold = 90;
        SystemMemoryChart.Lines = new[] { new ChartLine("系统内存", data.Select(x => (double?)x.SystemMemoryPercent).ToArray(), Color.FromRgb(255, 177, 88)) };
        var fpsValues = data.Where(x => x.Fps.HasValue).Select(x => x.Fps!.Value).ToArray();
        FpsChart.Maximum = Math.Max(60, fpsValues.DefaultIfEmpty(60).Max() * 1.15);
        FpsChart.Unit = "FPS · 平均帧间隔";
        FpsChart.Lines = fpsValues.Length == 0 ? Array.Empty<ChartLine>() : new[] { new ChartLine("FPS", data.Select(x => x.Fps).ToArray(), Color.FromRgb(90, 221, 168)) };
        FpsOverlay.Visibility = fpsValues.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        CpuChart.InvalidateVisual(); MemoryChart.InvalidateVisual(); SystemMemoryChart.InvalidateVisual(); FpsChart.InvalidateVisual();
    }

    private void FinishSession(string reason)
    {
        if (!_sessionRunning) return;
        _sessionRunning = false; _timer.Stop();
        _presentMon.Stop();
        StartButton.IsEnabled = true; ProcessCombo.IsEnabled = true; MemoryThresholdBox.IsEnabled = true; CpuThresholdBox.IsEnabled = true; StopButton.IsEnabled = false;
        SessionStatus.Text = "评测已结束"; StatusDot.Fill = new SolidColorBrush(Color.FromRgb(117, 134, 155));
        ElapsedText.Text = (DateTime.Now - _sessionStarted).ToString("hh\:mm\:ss");
        AddEvent("信息", "评测结束", reason);
        try
        {
            if (_sessionFolder is not null)
            {
                var csv = new StringBuilder("timestamp,game_cpu_percent,system_cpu_percent,game_working_set_mb,game_memory_percent,system_memory_percent,fps,frame_time_ms,gpu_busy_percent,hitch_count,worst_frame_time_ms\n");
                foreach (var s in _samples)
                    csv.AppendLine(string.Join(",", new[] { s.Time.ToString("O"), F(s.GameCpuPercent), F(s.SystemCpuPercent), F(s.GameMemoryMb), F(s.GameMemoryPercent), F(s.SystemMemoryPercent), F(s.Fps), F(s.FrameTimeMs), F(s.GpuBusyPercent), s.HitchCount.ToString(CultureInfo.InvariantCulture), F(s.WorstFrameTimeMs) }));
                File.WriteAllText(Path.Combine(_sessionFolder, "metrics.csv"), csv.ToString(), new UTF8Encoding(true));
                var summary = new
                {
                    app = "UE5 Performance Monitor",
                    sessionStarted = _sessionStarted,
                    sessionEnded = DateTime.Now,
                    durationSeconds = (DateTime.Now - _sessionStarted).TotalSeconds,
                    target = _targetProcess is null ? null : new { pid = _targetProcess.Id, name = Safe(() => _targetProcess.ProcessName) },
                    stopReason = reason,
                    sampleIntervalSeconds = 1,
                    thresholds = new { gameWorkingSetGb = _memoryThresholdGb, systemCpuPercent = _systemCpuThreshold, systemMemoryPercent = 90, lowFps = 30, severeFrameIntervalMs = 50 },
                    sampleCount = _samples.Count,
                    metrics = _samples.Count == 0 ? null : new
                    {
                        systemCpuAverage = _samples.Average(x => x.SystemCpuPercent),
                        systemCpuPeak = _samples.Max(x => x.SystemCpuPercent),
                        gameCpuPeak = _samples.Where(x => x.GameCpuPercent.HasValue).Select(x => x.GameCpuPercent!.Value).DefaultIfEmpty().Max(),
                        gameWorkingSetStartMb = _samples.First().GameMemoryMb,
                        gameWorkingSetPeakMb = _samples.Max(x => x.GameMemoryMb),
                        gameWorkingSetEndMb = _samples.Last().GameMemoryMb,
                        systemMemoryPeakPercent = _samples.Max(x => x.SystemMemoryPercent),
                        averageFps = _samples.Where(x => x.Fps.HasValue).Select(x => x.Fps!.Value).DefaultIfEmpty().Average(),
                        lowestSampledFps = _samples.Where(x => x.Fps.HasValue).Select(x => x.Fps!.Value).DefaultIfEmpty().Min(),
                        averageGpuBusyPercent = _samples.Where(x => x.GpuBusyPercent.HasValue).Select(x => x.GpuBusyPercent!.Value).DefaultIfEmpty().Average(),
                        severeHitchFrames = _samples.Sum(x => x.HitchCount),
                        worstFrameTimeMs = _samples.Where(x => x.WorstFrameTimeMs.HasValue).Select(x => x.WorstFrameTimeMs!.Value).DefaultIfEmpty().Max()
                    },
                    alerts = _alertEvents
                };
                File.WriteAllText(Path.Combine(_sessionFolder, "report.json"), JsonSerializer.Serialize(summary, JsonOptions), Encoding.UTF8);
                var targetName = _targetProcess is null ? "游戏进程" : Safe(() => _targetProcess.ProcessName) ?? "游戏进程";
                File.WriteAllText(Path.Combine(_sessionFolder, "report.html"), HtmlReportWriter.Create(_sessionStarted, DateTime.Now, targetName, reason, _samples, _alertEvents), new UTF8Encoding(true));
                OutputPathText.Text = $"报告已保存：{_sessionFolder}";
            }
        }
        catch (Exception ex) { MessageBox.Show($"报告生成失败：{ex.Message}", "保存错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        try { _targetProcess?.Dispose(); } catch { }
        _targetProcess = null;
    }

    private static string F(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    private static string Sanitize(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Replace(' ', '_');
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    protected override void OnClosed(EventArgs e)
    {
        if (_sessionRunning) FinishSession("关闭监控工具");
        base.OnClosed(e);
    }

    private sealed record ProcessChoice(int Pid, string Label, string ProcessName);
}
