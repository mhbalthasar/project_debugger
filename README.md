# project_debugger — 桥 / 隔离机制的跨平台验证台

这个仓库只做一件事：把 TuneLab 里 **宿主 ↔ 桥** 的进程间机制抽出来，在
Windows / Linux / macOS 的真机上跑**真进程**，回答两个问题：

1. 宿主被强杀（任务管理器 / `SIGKILL`）时，桥和它拉起的孙进程会不会一定跟着死？
2. 插件把桥搞死 / 卡死时，宿主会不会陪葬，下一次操作能不能起一座干净的桥？

它不验证 VST、不验证音频算法 —— 那部分换成了纯托管的假插件。

## 被验证的到底是什么

除了下面两处替换，其余代码是**从正式工程逐字复制**的：

| 文件 | 来源 | 说明 |
| --- | --- | --- |
| `UnixProcess.cs` | `InsertHost` | `setpgid` / `kill` / `pidfd` 等价物的 P/Invoke |
| `BridgeResidue.cs` | `InsertHost` | 强杀残骸清扫（段文件 + UDS socket） |
| `BridgeTimeout.cs` | `InsertHost` | 「发出去没回来就杀桥」 |
| `AudioStallWatch.cs` | `InsertHost` | 连续失败判死 |
| `IBridgeChannel.cs` | `InsertHost` | 桥通道接口 |
| `VstBridgeClient.cs` | `InsertHost` | 宿主侧：起进程、JobObject、存活管道、共享环、管道帧、超时杀桥 |
| `BridgeProtocol.cs` | `VstBridge` | 协议 DTO 与共享环布局常量 |
| `BridgeSharedMemory.cs` | `VstBridge` | 共享音频环（SPSC） |
| `BridgeSession.cs` | `VstBridge` | 消息循环（**只改了插件宿主的类型**） |
| `BridgeProgram.cs` | `VstBridge` → `src/DebugBridge/Program.cs` | 桥入口（**原样**） |

替换掉的两处：

- `PluginHost` / `Vst3PluginHost` → `FakePluginHost`（纯托管，路径即指令）。
- `BridgeSession.HandleHello` 里 `Vst3PluginHost.IsSupported` → 固定 `true`。

## 假插件的指令

`LoadPluginMessage.Path` 本来就只是一个字符串，于是直接当指令用：

| 路径 | 行为 |
| --- | --- |
| `gain:0.5` | 每块乘以 0.5（用来验证共享环真的把样本送过去了） |
| `hang` | 在 `Load` 里永远睡下去（验证 20s 加载超时 → 杀桥 → 还能重启） |
| `crash` | 在 `Load` 里 `Environment.Exit`（验证桥暴毙不拖死宿主） |
| `child` | 拉起一个长命子进程，把 PID 放进插件名（验证不留孤儿） |

## 测试清单

`tests/BridgeHarness.Tests/HarnessTests.cs`：

1. `StartsAndHandshakes` — 起桥、握手、自报 `vst2`+`vst3`
2. `GainIsAppliedThroughTheSharedRing` — 32 块 × 512 帧，逐样本对
3. `BypassPassesTheInputThrough`
4. `ManyBlocksGoThroughWithoutStalling` — 2000 块吞吐（只拦「卡死」，不是性能门槛）
5. `StateAndParametersRoundTrip`
6. `KillingTheHostTakesTheBridgeAndItsGrandchildDown` — **核心**：另起一个宿主进程，
   加载 `child`，然后 `Process.Kill()` 宿主；断言桥与孙进程在 15s 内都消失
7. `LivenessEofKillsTheBridge`（Unix）— 只放掉存活管道写端
8. `TheBridgeRunsInItsOwnProcessGroup`（Unix）— `pgid == pid` 且 ≠ 测试进程
9. `KillBridgeIsFinalAndRestartWorks`
10. `APluginThatCrashesTheBridgeDoesNotTakeTheHostDown`
11. `AHungPluginIsTimedOutAndTheBridgeIsKilled`（约 20s）
12. `AStalePipeSocketDoesNotBlockARestart`（Unix）
13. `ResidueSweepRemovesFilesOfDeadHostsAndKeepsLiveOnes`（Unix）

## 本地跑

```bash
dotnet test tests/BridgeHarness.Tests/BridgeHarness.Tests.csproj -c Release
```

测试会自己把 `DebugBridge` 摆到 `bridge/<rid>/`、把 `HostRunner` 摆到 `tools/hostrunner/`，
位置与 `VstBridgeClient.FindBridgeExecutable` 的约定一致。

## 第一轮结果（2026-10）

Windows x64：13/13 通过。

其中 `APluginThatCrashesTheBridgeDoesNotTakeTheHostDown` 一开始是**红**的，
它暴露了正式代码里的一个真实缺口：

> 控制管道读到 EOF 时，`VstBridgeClient` 只是返回 `null`，`mPipe` 仍然是「已连接」。
> `IsRunning` 继续为真，下一次 `EnsureStarted` 直接 `return true` —— 于是桥已经死了，
> 之后每一次加载都失败，直到有人显式 `KillBridge`（关混音台 / 重启应用）。

本仓库在 `ExchangeAsync` 里加了一处最小修正（见 `src/BridgeHarness/VstBridgeClient.cs`
里 `[debug-harness finding]` 注释）：回执为空就地 `KillBridge()`。**这处应当回灌到
TuneLab 本体**，否则「插件把桥搞崩一次」= 这个 VST3 插入直到重启都装不回来。

## 平台覆盖

| job | runner | 说明 |
| --- | --- | --- |
| win-x64 | `windows-latest` | JobObject |
| linux-x64 | `ubuntu-latest` | 存活管道 + 进程组 |
| osx-arm64 | `macos-latest` | 同上（第一次在真 macOS 上验证） |
| osx-x64 | `macos-15-intel` | 同上 |
| win-arm64 | `windows-11-arm` | 实验性 |
| linux-arm64 | `ubuntu-24.04-arm` | 实验性 |

## native 工作流

`.github/workflows/native.yml` 在 **AlmaLinux 8（glibc 2.28，与验证机一致）** 里
编译 `ModernMube/OwnVST3` 的 `libownvst3.so`，然后：

- 用 `objdump` 断言它引用的 GLIBC 符号版本不超过 2.28；
- 在同一个容器里 `dlopen` 一次，确认它真的能加载；
- 把 `.so` 作为构建产物上传。

这解决的是「上游 NuGet 里的 linux-x64 native 要求 GLIBC_2.38，Anolis 8.10 只有 2.28」
那条阻塞。
