# UE5 黑盒性能评测工具

Windows 桌面端原型：C# / .NET 8 / WPF。对正在运行的打包游戏进行外部黑盒采样，按整次评测输出时间序列、告警事件和异常快照；不注入游戏进程、不需要游戏源码。

## 当前实现

- 发现并选择有窗口的游戏进程；开始/结束一整次会话。
- 每秒采集系统 CPU、游戏进程 CPU、游戏进程工作集/私有内存、系统物理内存压力和进程线程数（线程数在异常快照中）。
- 可选配置 PresentMon 2.x x64 程序，使用其 ETW 帧呈现采集获取 FPS、平均帧间隔和 GPU Busy 估算；记录帧间隔不低于 50 ms 的严重卡顿帧数。
- 阈值告警：游戏工作集默认 8 GB（可调）、系统 CPU 默认 95%（可调）、系统内存压力 90%、进程 CPU 达单逻辑核心 100%、FPS 低于 30，以及严重帧间隔。
- CPU/内存/FPS/系统内存图表，近 120 个采样点（约 2 分钟）。内存基线增长超过 25% 时发出“观察”提示，不会把它直接判成泄漏。
- 触发告警时保存目标进程状态和触发前 15 秒的采样数据。注意这是**指标快照 JSON，不是进程内存 dump**。
- 一次评测结束后保存 `metrics.csv`、可视化 `report.html`、机器可读 `report.json` 和 PresentMon 原始 CSV（如果启用）；默认位于 `文档/UE5PerfMonitor/Sessions/<时间戳>/`。

## 构建

要求 Windows 10/11 x64、.NET 8 SDK。

```powershell
dotnet build .\src\UE5PerfMonitor\UE5PerfMonitor.csproj -c Release
dotnet run --project .\src\UE5PerfMonitor\UE5PerfMonitor.csproj
```

独立发布：

```powershell
dotnet publish .\src\UE5PerfMonitor\UE5PerfMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

FPS/GPU Busy 采集需要单独获取并选择 PresentMon 2.x 的 x64 控制台程序。ETW 会话权限因 Windows 配置而异；若采集进程无法启动/无数据，请尝试以管理员身份运行监控工具。工具本身默认不提权。

## 指标解释与范围

- 游戏 CPU 按 Windows 常用进程口径显示：一个逻辑核心满载为 100%，多核程序可以高于 100%；系统 CPU 是整机逻辑处理器归一化后的 0–100%。
- 游戏内存卡片显示工作集（当前驻留物理内存）；同时采样私有字节。报告不会声称单凭外部数据就能确定 UE GC 或确认内存泄漏。
- PresentMon 的 GPU Busy 是每帧 GPU 忙碌时间相对帧间隔的估算，**不等于显卡整体使用率，也不是显存占用**。
- 当前黑盒版本没有可靠的“进程级显存占用”、UE 内部 GC 对象回收诊断、NPC 加载阶段识别或通用穿模自动识别。此类能力需要 Windows GPU 计数器的适配验证，或游戏/测试脚本提供可观测信号；报告中应保持明确区分，不能用推测冒充检测结果。
- 50 ms 是默认严重卡顿提示线（约低于 20 FPS 的单帧间隔），不是所有项目的性能验收标准；应按目标帧率和游戏类型调整。
