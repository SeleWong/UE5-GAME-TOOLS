using System.Diagnostics;
using System.Runtime.InteropServices;
using UE5PerfMonitor.Models;

namespace UE5PerfMonitor.Services;

public sealed class SystemMetricsCollector
{
    private ulong _lastIdle, _lastKernel, _lastUser;
    private DateTime _lastRead;
    private TimeSpan? _lastProcessCpu;
    private int? _lastPid;

    public MetricSample Read(Process? process, double? fps = null, double? frameTimeMs = null, double? gpuBusyPercent = null, int hitchCount = 0, double? worstFrameTimeMs = null)
    {
        var now = DateTime.Now;
        var (idle, kernel, user) = ReadSystemTimes();
        var systemCpu = 0d;
        if (_lastRead != default)
        {
            var total = (kernel - _lastKernel) + (user - _lastUser);
            var idleDelta = idle - _lastIdle;
            if (total > 0) systemCpu = Math.Clamp((1d - (double)idleDelta / total) * 100d, 0, 100);
        }
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        _lastRead = now;

        var totalRam = GetTotalPhysicalMemory();
        var availableRam = GetAvailablePhysicalMemory();
        var systemMemoryPercent = totalRam == 0 ? 0 : Math.Clamp((double)(totalRam - availableRam) / totalRam * 100, 0, 100);

        double gameMb = 0, gamePercent = 0;
        double? gameCpu = null;
        if (process is { HasExited: false })
        {
            try
            {
                process.Refresh();
                gameMb = process.WorkingSet64 / 1024d / 1024d;
                gamePercent = totalRam == 0 ? 0 : gameMb * 1024 * 1024 / totalRam * 100;
                var cpu = process.TotalProcessorTime;
                var elapsed = _previousSampleTime == default ? 0 : (now - _previousSampleTime).TotalSeconds;
                // Windows process CPU convention: one fully occupied logical core equals 100%; multicore may exceed 100%.
                if (_lastPid == process.Id && _lastProcessCpu.HasValue && elapsed > 0)
                    gameCpu = Math.Max(0, (cpu - _lastProcessCpu.Value).TotalSeconds / elapsed * 100);
                _lastPid = process.Id;
                _lastProcessCpu = cpu;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        _previousSampleTime = now;
        return new MetricSample(now, gameCpu, systemCpu, gameMb, gamePercent, systemMemoryPercent, fps, frameTimeMs, gpuBusyPercent, hitchCount, worstFrameTimeMs);
    }

    private DateTime _previousSampleTime;

    private static (ulong Idle, ulong Kernel, ulong User) ReadSystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return default;
        return (ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static ulong ToUInt64(FILETIME value) => ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;

    private static ulong GetTotalPhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys : 0;
    }

    private static ulong GetAvailablePhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
