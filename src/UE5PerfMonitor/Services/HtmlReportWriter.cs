using System.Globalization;
using System.Net;
using System.Text;
using UE5PerfMonitor.Models;

namespace UE5PerfMonitor.Services;

public static class HtmlReportWriter
{
    public static string Create(DateTime started, DateTime ended, string target, string stopReason, IReadOnlyList<MetricSample> samples, IReadOnlyList<AlertEvent> alerts)
    {
        var html = new StringBuilder();
        var fps = samples.Where(x => x.Fps.HasValue).Select(x => x.Fps!.Value).ToArray();
        var maxCpu = samples.Count == 0 ? 0 : samples.Max(x => x.SystemCpuPercent);
        var peakMemory = samples.Count == 0 ? 0 : samples.Max(x => x.GameMemoryMb) / 1024;
        html.Append("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>UE5 黑盒评测报告</title><style>");
        html.Append("body{margin:0;background:#0b111b;color:#e5edf7;font:14px Segoe UI,Microsoft YaHei,sans-serif}.wrap{max-width:1250px;margin:auto;padding:32px}.muted{color:#91a2b7}.panel{background:#141d2b;border:1px solid #202c3d;border-radius:12px;padding:18px;margin:16px 0}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}.card{background:#141d2b;border-radius:10px;padding:16px}.value{font-size:28px;font-weight:700;margin-top:8px}.chart{width:100%;height:auto;display:block}.event{padding:10px 0;border-bottom:1px solid #263448}.tag{color:#71d5b2}h1{margin:6px 0 8px}h2{font-size:16px}a{color:#71d5b2}");
        html.Append("</style><div class=\"wrap\"><div class=\"tag\">UE5 PERFORMANCE MONITOR · BLACK-BOX REPORT</div><h1>整次运行性能报告</h1><div class=\"muted\">");
        html.Append(E(target)).Append(" · ").Append(E(started.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" 至 ").Append(E(ended.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" · ").Append(E(stopReason)).Append("</div><section class=\"cards\">");
        Card(html, "系统 CPU 峰值", $"{maxCpu:0}%");
        Card(html, "游戏工作集峰值", $"{peakMemory:0.00} GB");
        Card(html, "平均 FPS", fps.Length == 0 ? "未采集" : $"{fps.Average():0.0}");
        Card(html, "最低采样 FPS", fps.Length == 0 ? "未采集" : $"{fps.Min():0.0}");
        Card(html, "严重卡顿帧 (≥50 ms)", samples.Sum(x => x.HitchCount).ToString(CultureInfo.InvariantCulture));
        Card(html, "告警事件", alerts.Count.ToString(CultureInfo.InvariantCulture));
        html.Append("</section><section class=\"panel\"><h2>CPU 使用率 (%)</h2>");
        var cpuMax = Math.Max(100, samples.Where(x => x.GameCpuPercent.HasValue).Select(x => x.GameCpuPercent!.Value).DefaultIfEmpty(100).Max() * 1.1);
        Chart(html, samples, cpuMax, ("系统", x => x.SystemCpuPercent, "#5baaff"), ("游戏进程", x => x.GameCpuPercent, "#5adda8"));
        html.Append("<div class=\"muted\">游戏进程 CPU 按单逻辑核心满载 = 100% 计，多核可超过 100%。</div></section><section class=\"panel\"><h2>游戏进程内存工作集 (GB)</h2>");
        Chart(html, samples, Math.Max(1, peakMemory * 1.1), ("工作集", x => x.GameMemoryMb / 1024, "#c485ff"));
        html.Append("</section><section class=\"panel\"><h2>FPS（PresentMon 可选采集）</h2>");
        Chart(html, samples, Math.Max(60, fps.DefaultIfEmpty(60).Max() * 1.1), ("FPS", x => x.Fps, "#5adda8"));
        html.Append("</section><section class=\"panel\"><h2>告警与异常快照</h2>");
        if (alerts.Count == 0) html.Append("<div class=\"muted\">本次未触发阈值告警。</div>");
        foreach (var alert in alerts.OrderBy(x => x.Time))
            html.Append("<div class=\"event\"><b>").Append(E(alert.Time.ToString("HH:mm:ss"))).Append(" · ").Append(E(alert.Severity)).Append(" · ").Append(E(alert.Title)).Append("</b><div class=\"muted\">").Append(E(alert.Detail)).Append("</div></div>");
        html.Append("</section><section class=\"panel muted\"><b>解读边界：</b>内存增长是风险提示，不等同于已确认泄漏；PresentMon GPU Busy 不等于显卡整体利用率或显存占用。报告由外部黑盒指标生成，不含 UE 内部 GC/NPC 对象诊断。</section><div class=\"muted\">详细秒级数据：<a href=\"metrics.csv\">metrics.csv</a> · 机器可读摘要：<a href=\"report.json\">report.json</a></div></div></html>");
        return html.ToString();
    }

    private static void Card(StringBuilder html, string label, string value) => html.Append("<div class=\"card\"><div class=\"muted\">").Append(E(label)).Append("</div><div class=\"value\">").Append(E(value)).Append("</div></div>");

    private static void Chart(StringBuilder html, IReadOnlyList<MetricSample> samples, double maximum, params (string Name, Func<MetricSample, double?> Value, string Color)[] lines)
    {
        const double left = 48, right = 985, top = 15, bottom = 205;
        html.Append("<svg class=\"chart\" viewBox=\"0 0 1000 240\" role=\"img\">");
        for (var i = 0; i <= 4; i++)
        {
            var y = top + (bottom - top) * i / 4;
            html.Append("<line x1=\"48\" x2=\"985\" y1=\"").Append(y.ToString("0.0", CultureInfo.InvariantCulture)).Append("\" y2=\"").Append(y.ToString("0.0", CultureInfo.InvariantCulture)).Append("\" stroke=\"#2b3748\"/><text x=\"5\" y=\"").Append((y + 4).ToString("0.0", CultureInfo.InvariantCulture)).Append("\" fill=\"#91a2b7\" font-size=\"11\">").Append((maximum * (4 - i) / 4).ToString("0.#", CultureInfo.InvariantCulture)).Append("</text>");
        }
        foreach (var line in lines)
        {
            var points = new List<string>();
            for (var i = 0; i < samples.Count; i++)
            {
                var value = line.Value(samples[i]);
                if (!value.HasValue) continue;
                var x = left + (samples.Count <= 1 ? 0 : (double)i / (samples.Count - 1) * (right - left));
                var y = bottom - Math.Clamp(value.Value / maximum, 0, 1) * (bottom - top);
                points.Add($"{x.ToString("0.0", CultureInfo.InvariantCulture)},{y.ToString("0.0", CultureInfo.InvariantCulture)}");
            }
            if (points.Count > 0) html.Append("<polyline fill=\"none\" stroke=\"").Append(line.Color).Append("\" stroke-width=\"2.5\" points=\"").Append(string.Join(' ', points)).Append("\"/>");
        }
        html.Append("</svg><div class=\"muted\">");
        foreach (var line in lines) html.Append("<span style=\"color:").Append(line.Color).Append(";margin-right:18px\">● ").Append(E(line.Name)).Append("</span>");
        html.Append("</div>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
