# project_debugger — 桥 / 隔离机制的跨平台验证台

这个仓库只做两件事：

1. 把 TuneLab 里 **宿主 ↔ 桥** 的进程间机制抽出来，在 Windows / Linux / macOS 的真机上跑
   **真进程**，回答「宿主被强杀时桥会不会跟着死」「插件把桥搞死 / 卡死时宿主会不会陪葬」。
2. 在同一座桥上装**真的 VST3 插件**（真的 `ownvst3` 原生库 + 真的 `.vst3` 包），
   回答「真插件在 Linux / macOS 上是不是和 Windows 一样稳」。

第 1 件事用纯托管的假插件，第 2 件事用**逐字复制的正式 VST3 宿主代码**。

## 被验证的到底是什么

### 第 1 组：进程与管道（假插件）

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
| `BridgeSession.cs` | `VstBridge` | 消息循环（**只改了插件宿主的类型 + 发现 5 的回执**） |
| `BridgeProgram.cs` | `VstBridge` → `src/DebugBridge/Program.cs` | 桥入口（**只改了管道缓冲大小**） |

替换掉的两处：

- `PluginHost` / `Vst3PluginHost` → `FakePluginHost`（纯托管，路径即指令）。

### 第 2 组：真 VST3（真插件）

这一组里 `FakePluginHost` 只负责非 `.vst3` 的路径；路径以 `.vst3` 结尾时走
`RealVst3PluginHost`——它是正式工程 `Vst3PluginHost.cs` 的逐字复制品（只删掉了 P2 的
编辑器窗口那条路：验证台没有 GUI，窗口泵在 macOS / Linux 上也还没落地）。

| 目录 | 来源 | 说明 |
| --- | --- | --- |
| `src/RealVst3/InsertHost/Vst3Backend.cs` | `InsertHost` | VST3 后端（删掉了「桥里再套一层桥」的隔离分支） |
| `src/RealVst3/InsertHost/Vst3Insert.cs` | `InsertHost` | 音频线程上的 VST3 实例 |
| `src/RealVst3/InsertHost/Vst3BundleInfo.cs` | `InsertHost` | 包结构解析（三平台的 `Contents/<arch>` 约定） |
| `src/RealVst3/InsertHost/IVst3Insert.cs` / `IInsertBackend.cs` | `InsertHost` | 宿主侧接口 |
| `src/RealVst3/MixerEngine/IMixInsert.cs` | `MixerEngine` | 引擎的插入接口 |
| `src/RealVst3/Vendored/*.cs` | `InsertHost/Vendored` | OwnVST3 的托管包装层（P/Invoke，20 个 `VST3Plugin_*`） |
| `src/RealVst3/RealVst3PluginHost.cs` | `VstBridge` 的 `Vst3PluginHost.cs` | 桥进程里的真 VST3 宿主（去掉编辑器） |

原生库**不进仓库**（60MB 二进制）：构建时用环境变量 `Vst3NativeRoot` 指过去，
目录约定 `Vst3NativeRoot/<rid>/<ownvst3.tlhost.dll | libownvst3.tlhost.so | libownvst3.tlhost.dylib>`
（与 `Vst3Backend.NativeRid` 一致）。CI 从 NuGet 的 `OwnAudioVst` 1.7.0 里解出**与发布版同一份**
二进制再改成 `.tlhost` 名字；开发机可以直接指向 TuneLab 的 `ExperimentalPatch/InsertHost/Native/`。

没有原生库时，真 VST3 那组测试会打印一行 SKIP 后跳过，其余照跑。

## 真 VST3 测试插件

`tests/plugins/testvst3/` 是一个**最小但真实**的 VST3 效果器（CMake + Steinberg VST3 SDK，
不依赖 VSTGUI）。行为：立体声进 / 立体声出，一个 `Gain` 参数（0..1，默认 0.5）直接乘到样本上，
state 就是 4 字节浮点。五个变体：

| 包 | 行为 |
| --- | --- |
| `TestGain.vst3` | 正常增益（用来逐样本断言「声音真的穿过了桥」） |
| `TestCrashOnProcess.vst3` | 第一次 `process` 里 `abort()` —— 真插件把桥弄崩 |
| `TestHangOnProcess.vst3` | 第一次 `process` 里永远睡 —— 真插件把桥卡死 |
| `TestCrashOnLoad.vst3` | `initialize` 里 `abort()` —— 加载期崩溃 |
| `TestHangOnLoad.vst3` | `initialize` 里永远睡 —— 加载期卡死 |

自己编插件而不是用现成的，理由只有三条：runner 上没有任何 VST3；只有自己写的才能**故意**
崩 / 卡；行为确定，所以可以逐样本断言，而不是「跑完没报错」。

## 假插件的指令

`LoadPluginMessage.Path` 本来就只是一个字符串，于是直接当指令用：

| 路径 | 行为 |
| --- | --- |
| `gain:0.5` | 每块乘以 0.5（用来验证共享环真的把样本送过去了） |
| `hang` | 在 `Load` 里永远睡下去（验证 20s 加载超时 → 杀桥 → 还能重启） |
| `crash` | 在 `Load` 里 `Environment.Exit`（验证桥暴毙不拖死宿主） |
| `child` | 拉起一个长命子进程，把 PID 放进插件名（验证不留孤儿） |

## 测试清单

`tests/BridgeHarness.Tests/HarnessTests.cs`（进程与管道）：

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

`tests/BridgeHarness.Tests/RealVst3Tests.cs`（真 VST3，需要原生库 + 插件包）：

1. `LoadsARealVst3AndProcessesAudio` — 加载真包、读参数、把 `Gain` 调成 0.25，
   逐样本断言输出 = 输入 × 0.25（**证明真插件的声音真的走完了整条桥**）
2. `RealVst3StateSurvivesTheRoundTrip` — 存 state → 改参数 → 灌回 state → 声音回到 0.25
3. `ARealPluginCrashingInProcessOnlyTakesTheBridgeDown` — 真插件 `abort()`：宿主活着，
   死桥被判死，重建后能装回好插件
4. `ARealPluginHangingInProcessStallsTheAudioPathOnly` — 真插件卡死：音频路 1s 内判死、
   之后一律直通，杀桥后可重建
5. `ARealPluginCrashingWhileLoadingIsReported` — 加载期崩溃：失败回执 + 可重建
6. `ARealPluginHangingWhileLoadingIsTimedOut` — 加载期卡死：20s 超时杀桥，之后可重建
7. `UnloadingARealVst3IsCleanAndTheSlotCanBeReused` — 卸载不挂，槽位可复用

## 本地跑

```bash
# 只有进程机制
dotnet test tests/BridgeHarness.Tests/BridgeHarness.Tests.csproj -c Release

# 加上真 VST3（Windows 示例）
$env:Vst3NativeRoot = 'Y:\...\ExperimentalPatch\InsertHost\Native'   # <rid>/ownvst3.tlhost.dll
$env:TL_VST3_BUNDLE_DIR = 'C:\...\vst3plugin\bundles'
dotnet test tests/BridgeHarness.Tests/BridgeHarness.Tests.csproj -c Release
```

测试会自己把 `DebugBridge` 摆到 `bridge/<rid>/`、把 `HostRunner` 摆到 `tools/hostrunner/`，
位置与 `VstBridgeClient.FindBridgeExecutable` 的约定一致。

编插件（需要 vst3sdk 检出）：

```bash
cmake -S tests/plugins/testvst3 -B build -DCMAKE_BUILD_TYPE=Release -DVST3_SDK=/path/to/vst3sdk
cmake --build build --parallel
# 产物：build/bundles/<Name>.vst3
```

`HostRunner hangprobe <bundle>` 会把「卡在哪一步」每 2 秒打到 stderr —— 排查真插件卡死时用。

## 结果（2026-10-31，第一轮 CI：进程与管道）

| job | 结果 | 说明 |
| --- | --- | --- |
| linux-x64 | ✅ 13/13 | 存活管道 + 看门狗 + 进程组 + 残留清扫，全部真的跑通 |
| linux-arm64 | ✅ 13/13 | 同上 |
| osx-arm64 | ✅ 13/13 | **第一次在真 macOS 上验证** |
| osx-x64 | ✅ 13/13 | 同上 |
| win-x64 | ❌ → 已修 | 见「发现 2」 |
| win-arm64 | ❌ → 已修 | 见「发现 3」 |
| native / glibc 2.28 | ✅ | `.so` 编译成功、`dlopen` 成功、引用的 GLIBC 符号 ≤ 2.28 |

## 结果（2026-11-01，第二轮：真 VST3，Windows 本机）

```
RealVst3Tests: 7/7 通过（约 30s，其中 20s 是加载期卡死的超时）
HarnessTests : 13/13 通过
```

逐样本断言成立：`Gain = 0.25` 时输出确实是输入的 1/4，state 往返之后又回到 1/4。
Linux / macOS 的同一组跑在 CI 里（见下）。

## 结果（2026-11-01，第三轮：真 VST3，六平台 CI 全绿）

第一轮（`7bf9802`）跑出了发现 6、发现 7 和 macOS 的两件事；修完之后（`53ffff5`）：

| job | 测试 | 进程机制 | 真 VST3 | 说明 |
| --- | --- | --- | --- | --- |
| win-x64 | 20/20 | ✅ | ✅ | JobObject；真插件加载 + 逐样本增益 |
| linux-x64 | 20/20 | ✅ | ✅ | 存活管道 + 进程组 + 残留清扫 |
| linux-arm64 | 20/20 | ✅ | ✅ | 同上（实验性 runner） |
| osx-arm64 | 20/20 | ✅ | ✅ | 真 macOS |
| osx-x64 | 20/20 | ✅ | ✅ | 真 macOS（Intel） |
| win-arm64 | 20/20 | ✅ | —（跳过） | 实验性；testhost 是 x64，原生库架构会配错，`vst3: false` |

结论：**真 VST3 插件的加载 / 处理 / state / 崩溃 / 卡死 / 卸载，在 Linux 与 macOS 上和
Windows 一样稳** —— 逐样本断言（输出 = 输入 × 0.25）在四个平台上都成立，
五个故障变体的隔离结论也一致。

## 发现

### 发现 1：控制管道 EOF 之后，客户端仍认为桥活着

`APluginThatCrashesTheBridgeDoesNotTakeTheHostDown` 一开始是红的。

> 控制管道读到 EOF 时，`VstBridgeClient` 只是返回 `null`，`mPipe` 仍然是「已连接」。
> `IsRunning` 继续为真，下一次 `EnsureStarted` 直接 `return true` —— 于是桥已经死了，
> 之后每一次加载都失败，直到有人显式 `KillBridge`（关混音台 / 重启应用）。

修正（`src/BridgeHarness/VstBridgeClient.cs`，`[debug-harness finding]`）：`ExchangeAsync`
拿到空回执就地 `KillBridge()`。**应当回灌 TuneLab 本体** —— 否则「插件把桥搞崩一次」
等于这个 VST3 插入直到重启都装不回来。

### 发现 2：杀桥之后立刻重建共享段，会撞上「文件已存在」

`KillBridgeIsFinalAndRestartWorks` 在 CI 的 win-x64 上稳定复现，本地却常常是绿的：

> `KillBridge` 调 `Process.Kill(entireProcessTree: true)` 之后**不等进程退出**就
> `Dispose` 了 `mInput` / `mOutput`。Windows 的共享段是内核对象，由两端句柄共同持有：
> 桥还活着的那几毫秒里，下一次 `EnsureStarted` 的 `MemoryMappedFile.CreateNew`
> 会抛 "Cannot create a file when that file already exists."。

修正：杀完 `WaitForExit(2000)` 再放段。**同样应当回灌**。

### 发现 3：win-arm64 上「构建 RID」与「运行 RID」不一致

arm64 runner 上 `NETCoreSdkPortableRuntimeIdentifier` 是 `win-arm64`，但 `dotnet test`
的 testhost 是 x64，运行时里 `VstBridgeClient.BridgeRid` 算出 `win-x64`，于是找不到桥。
这不是产品缺陷（正式发布是一套 RID 一整套产物），验证台把桥同时摆到同一 OS 的两个
架构目录下即可。

### 发现 4：隔离模式下**任何真 VST3 都装不上**（`File.Exists` 对目录包恒为 false）

`LoadsARealVst3AndProcessesAudio` 第一次跑就红：

> 桥侧 `Vst3PluginHost.Load` 用 `File.Exists(request.Path)` 判断插件在不在，
> 而 VST3 在三个平台上都是**目录包**（`<Name>.vst3/Contents/<arch>/...`），
> `File.Exists` 对目录永远返回 false —— 于是隔离模式下一个真 VST3 都装不上，
> 统一报「plugin file not found」。进程内那条路（`Vst3Backend` 直接把 Locator
> 交给 native，不做存在性检查）所以从来没暴露过。

修正：`File.Exists(path) || Directory.Exists(path)`。**必须回灌 TuneLab 本体** ——
这条是「P3 打开隔离就全挂」的严重级别。

### 发现 5：插件卡死时，**宿主的音频线程被无限期阻塞**

`ARealPluginHangingInProcessStallsTheAudioPathOnly` 一开始直接把 testhost 挂到看门狗
（`--blame-hang`）出手才结束 —— 也就是说音频线程真的死在那里。两处叠加：

> 一是音频路径上那次「读 ProcessRequest 回执」用的是 `ReadControlBlocking()`，
> **没有超时**。桥的主循环卡在插件的 `process` 里时，回执永远不来，音频线程就永远等。
> 二是控制管道在 Windows 上没有缓冲（`NamedPipeServerStream` 不显式给
> `inBufferSize/outBufferSize` 时是同步管道）：写进去的每一帧都要等桥**读走**才返回，
> 于是第二块连写都写不进去。Linux/macOS 的 UDS 有内核缓冲，所以只有第一条会发作，
> 但第一条就足够把宿主拖死。

修正：

- `BridgeSession.HandleProcess` **不再回执**（处理结果本来就在共享环里，宿主等输出环
  就够了，回执是多余的一次同步点）；
- 桥的 `NamedPipeServerStream` 显式要 64 KiB 缓冲（客户端的构造重载没有缓冲参数，
  写方向用的就是服务端的 inbound buffer）。

修正之后：卡死的真插件在 50 块（约 1s）内被判死、之后一律直通，杀桥后立刻可重建。
**两条都应当回灌 TuneLab 本体**。

### 发现 6：Unix 上「管道写抛异常」不被当成桥死了（Windows 上发现不了）

`ARealPluginCrashingInProcessOnlyTakesTheBridgeDown` 在 linux-x64 / linux-arm64 上是红的，
win-x64 / osx 上是绿的：

> 桥被插件 `abort()` 带走之后，宿主下一次 `LoadBlocking` 应该返回 null **并且**把桥判死。
> Windows 命名管道的 `IsConnected` 在服务端进程消失后会变 false，所以 `TryLoad*` 开头的
> `if (!IsRunning)` 就把它挡掉了；而 Unix（UDS 模拟）的 `IsConnected` **一直为真**，
> 于是一路走到写，拿到 EPIPE → IOException。这个异常本来只被 `TryLoad*` 的 catch 吞掉、
> 返回 null，不判死 —— 桥明明死了，`IsRunning` 还是真，之后每次加载都失败，
> 直到重启应用。这正是「发现 1」在 Unix 上的另一条到达路径。

修正：`ExchangeAsync` 里把写/读抛出的异常（`OperationCanceledException` 除外）也当作死亡，
就地 `KillBridge()` 并返回 null。**应当回灌 TuneLab 本体**。

### 发现 7：两处同时杀桥 → 共享段被 Dispose 两次

`AHungPluginIsTimedOutAndTheBridgeIsKilled` 在 win-arm64 上抛
`ObjectDisposedException`（`SafeMemoryMappedViewHandle`，栈在
`SharedAudioRing.Dispose` → `MemoryMappedViewAccessor.Dispose` → `Flush`）：

> 超时那条路是**两个线程**同时杀桥：`BridgeTimeout.Run` 的 `onTimeout`（超时线程），
> 以及被打断的那条交换任务（管道被 Dispose 之后它读到 EOF，也走 `KillBridge`）。
> 两边都可能看到非 null 的 `mInput` / `mOutput` 并各 Dispose 一次，第二次撞在已释放的
> 映射句柄上。arm64 上稳定复现，x64 只是时间窗口更小。

修正：`KillBridge` 串行化（`mKillLock`），`KillBridgeCore` 才是原来的方法体。
**应当回灌 TuneLab 本体**。

### 顺带：macOS 上手写 `add_library` 少了平台框架与平台入口

**第一件**：osx-arm64 / osx-x64 的插件编译一开始全红：`_CFStringCreateWithFormat`、`_CFUUIDCreate`
等一片 `_CF*` 未定义符号。

> `smtg_add_vst3plugin` 会替你把 `-framework CoreFoundation` 接上；本项目为了控制产物位置
> 用的是手写 `add_library(MODULE)`，于是这一步要自己补。VST3 的 base 实现用 CoreFoundation
> 处理 UTF-16 字符串，缺了它链接必失败。

修正：`CMakeLists.txt` 里 `if(SMTG_MAC) target_link_libraries(... "-framework CoreFoundation")`。

**第二件（更阴）**：链接过了之后，macOS 上五个测试全报
“the plugin could not be loaded (bitness or a missing dependency)”，而包结构、架构、签名
逐项检查都对。

> `smtg_add_vst3plugin` 还会经 `smtg_target_add_library_main` 加上**平台入口源文件**：
> macOS 的 `public.sdk/source/main/macmain.cpp`（定义 `bundleEntry` / `bundleExit`）、
> Linux 的 `linuxmain.cpp`、Windows 的 `dllmain.cpp`。
> Windows / Linux 上宿主是直接 `dlsym` / `GetProcAddress` 取 `GetPluginFactory`，所以缺了
> 入口文件也能加载 —— 只有 macOS 会挂：VST3 在 macOS 上由 CFBundle 取 `bundleEntry`，
> 缺了这个符号就是「加载失败」，且报出来的原因和"架构不对"完全一样。

修正：按平台把对应入口文件加进 `target_sources`。

### 顺带：帧上限的闸，两端都补上了（产品侧 P5.5）

验证台把隔离从"默认关"推到"默认开"之后，`state` 一律走这条帧（`4 字节长度 + UTF-8 JSON +
base64`）。两端都拿 `MaxFrameBytes = 16 MB` 判非法，可是**写的那一端原来不拦**：

> 超上限的帧照样写出去，对端读长度时判非法、只读走那 4 个字节就返回 —— 剩下的字节全留在
> 管道里，之后每一帧都对不上，等于把整座桥废掉（比"这一次失败"糟得多）。

修正（两侧对称）：宿主 `VstBridgeClient.ExchangeAsync` 先量字节数、超了就当场失败并记
`LastFrameError`；桥 `BridgeSession.Write` 超上限时改回一条合法的 `ErrorMessage`。
实际天花板 ≈ 12 MB 原始 state（base64 之后），常见 VST3 state 只有几十 KB，所以这条闸平时不响。
本仓库的两份副本已同步，行为与 TuneLab 本体一致。

### 顺带：macOS x64 上偶发"丢一块"，测试改成容忍一次过载

`74a466c` 那一轮 osx-x64 的 `GainIsAppliedThroughTheSharedRing` 与
`ManyBlocksGoThroughWithoutStalling` 各失败一次，报 `SendProcess failed on block 1`；
同一提交在 win-x64 / win-arm64 / linux-x64 / linux-arm64 / osx-arm64 上全绿，本机也 20/20。

> 这不是隔离机制的问题，而是**测试口径**问题：`SendProcess` 的实时纪律是"20 ms 内没赶上就
> 直通"，共享 macOS runner 上一块音频偶尔超过 20 ms 是产品语义允许的结果（那一块直通），
> 但测试原来把它当零容忍。2000 块的循环尤其脆：一次抖动就整条红。

修正：`Harness.SendBlock` 重试一次再判失败（断言照样逐样本比增益）；2000 块那条改成
**统计丢块率**（上限 0.5%），仍能一把抓住"音频路径整体不通"或"连续卡死"。
"必须失败"的用例（杀桥 / 卡死）继续直接用 `Client.SendProcess`。

## 平台覆盖

| job | runner | 进程机制 | 真 VST3 | 说明 |
| --- | --- | --- | --- | --- |
| win-x64 | `windows-latest` | ✅ | ✅ | JobObject |
| linux-x64 | `ubuntu-latest` | ✅ | ✅ | 存活管道 + 进程组 |
| osx-arm64 | `macos-latest` | ✅ | ✅ | 第一次在真 macOS 上验证 |
| osx-x64 | `macos-15-intel` | ✅ | ✅ | 同上 |
| win-arm64 | `windows-11-arm` | ✅ | — | 实验性；testhost 是 x64，原生库架构会配错 |
| linux-arm64 | `ubuntu-24.04-arm` | ✅ | ✅ | 实验性 |

## native 工作流

`.github/workflows/native.yml` 在 **AlmaLinux 8（glibc 2.28，与验证机一致）** 里
编译 `ModernMube/OwnVST3` 的 `libownvst3.so`，然后：

- 用 `objdump` 断言它引用的 GLIBC 符号版本不超过 2.28（实测上限 **2.17**）；
- 在同一个容器里 `dlopen` 一次，确认它真的能加载；
- 断言它导出 TuneLab 的 managed 包装层需要的那 20 个 `VST3Plugin_*` 符号
  （公开源码 1.5.4 导出 33 个，是 NuGet 1.7.0 native 所用 20 个的超集）；
- 把 `.so` 作为构建产物上传。

这解决的是「上游 NuGet 里的 linux-x64 native 要求 GLIBC_2.38，Anolis 8.10 只有 2.28」
那条阻塞。

实测（libr 679 KB）：

```
max GLIBC symbol version = 2.17      # GLIBCXX 上限 3.4.21
dlopen ok: 0x3e0442d0
```

即：**在 Anolis 8.10（glibc 2.28）上可以直接用**，甚至能下探到 glibc 2.17。

已知限制：公开源码是 1.5.4，而当前 vendored 的 native 来自 NuGet 1.7.0；
两者导出名兼容，但 1.6/1.7 的行为修复未必在 1.5.4 里。真 VST3 那一组用的是 NuGet 1.7.0
的二进制（与发布版同一份），所以它验证的是**会随软件发出去的那个 `.so`**；
把这条自编译线接进发布，还需要单独确认 1.5.4 的行为等价。

## 许可

验证台自身的代码与 TuneLab 同一来源。`tests/plugins/testvst3/` 里的测试插件包含
Steinberg VST3 SDK 的头文件与公共实现，随 SDK 的双许可（GPLv3 或 Steinberg 专有）
约束；它只在 CI 里编出来用于测试，不随任何产品发布。
