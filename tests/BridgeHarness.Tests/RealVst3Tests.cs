using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;
using Xunit;
using Xunit.Abstractions;

namespace BridgeHarness.Tests;

// 真 VST3 的端到端验证：真的 ownvst3 原生库 + 真的 .vst3 包，穿过桥进程。
//
// 【为什么这组和 HarnessTests 分开】HarnessTests 用假插件钉"进程与管道"；
// 这组钉的是"在 Linux / macOS 上，真插件能不能像 Windows 上一样装进桥、出声、
// 存取状态，以及真插件崩了 / 卡了会不会只死桥"。前一组里的宿主实现是假的，
// 这一组里的宿主实现（Vst3PluginHost / Vst3Backend / Vst3Insert）是从正式工程逐字复制的。
//
// 【为什么默认跳过】需要三样东西同时就位：
//   1. 桥 exe（与 HarnessTests 同一判据）；
//   2. 桥目录里有原生库（Vst3NativeRoot 参与构建时才会摆进去）；
//   3. 真的 .vst3 包（TL_VST3_BUNDLE_DIR 指向 tests/plugins/testvst3 的构建产物）。
// 缺任何一样就跳过，并在输出里写清缺的是哪一样。
public class RealVst3Tests
{
    readonly ITestOutputHelper mOutput;

    public RealVst3Tests(ITestOutputHelper output) => mOutput = output;

    // 测试包目录（CMake 的 build/bundles）。
    static string? BundleDir
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("TL_VST3_BUNDLE_DIR");
            return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) ? null : dir;
        }
    }

    // 桥进程用的原生库文件名（与 Vst3Backend.NativeFileName 同一套规则）。
    static string NativeFileName =>
        OperatingSystem.IsWindows() ? "ownvst3.tlhost.dll" :
        OperatingSystem.IsMacOS() ? "libownvst3.tlhost.dylib" :
        "libownvst3.tlhost.so";

    static string BridgeDirectory => Path.GetDirectoryName(TestEnvironment.BridgePath)!;

    static bool NativeBesideBridge => File.Exists(Path.Combine(BridgeDirectory, NativeFileName));

    bool Ready()
    {
        if (!TestEnvironment.BridgeAvailable)
        {
            mOutput.WriteLine($"SKIP: no bridge executable at {TestEnvironment.BridgePath}");
            return false;
        }

        if (!NativeBesideBridge)
        {
            mOutput.WriteLine($"SKIP: no VST3 host library at {BridgeDirectory} (set Vst3NativeRoot when building)");
            return false;
        }

        if (BundleDir == null)
        {
            mOutput.WriteLine("SKIP: TL_VST3_BUNDLE_DIR is not set or does not exist");
            return false;
        }

        return true;
    }

    static string Bundle(string name)
    {
        string path = Path.Combine(BundleDir!, name + ".vst3");
        Assert.True(Directory.Exists(path), $"missing test plugin bundle: {path}");
        return path;
    }

    static (float[][] Input, float[][] Output) Buffers(int frames)
    {
        var input = new[] { new float[frames], new float[frames] };
        var output = new[] { new float[frames], new float[frames] };

        for (int i = 0; i < frames; i++)
        {
            input[0][i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
            input[1][i] = 0.25f - input[0][i];
        }

        return (input, output);
    }

    // ── 1. 加载 + 处理：把增益调成 0.25，输出必须逐样本是输入的 1/4 ──

    [Fact]
    public void LoadsARealVst3AndProcessesAudio()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start(Bundle("TestGain"), format: PluginFormats.Vst3);

        mOutput.WriteLine($"loaded '{harness.PluginName}' vendor='{harness.Load.Vendor}' " +
                          $"in={harness.Load.InputChannels} out={harness.Load.OutputChannels} " +
                          $"latency={harness.Load.LatencySamples} editor={harness.Load.HasEditor}");

        Assert.Equal(2, harness.Load.InputChannels);
        Assert.Equal(2, harness.Load.OutputChannels);

        // 我们的测试插件只有一个参数 Gain（0..1；VST3 的参数值本来就是归一化的，
        // 于是归一化值和线性增益是同一个数）。
        IReadOnlyList<ParameterInfo>? parameters = harness.Client.GetParametersBlocking(harness.Slot);
        Assert.NotNull(parameters);

        ParameterInfo gain = parameters!.First(p => p.Name == "Gain");

        Assert.True(harness.Client.SetParameterBlocking(harness.Slot, gain.Id, 0.25));

        const int frames = 512;
        var (input, output) = Buffers(frames);

        // 参数改动要等一块 process 才被插件消费（VST3 的标准路径），所以多跑几块、
        // 断言最后一块 —— 这也顺带证明"改参数之后声音真的变了"，不是只写进缓存。
        for (int block = 0; block < 4; block++)
        {
            Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false),
                "SendProcess failed on block " + block);
        }

        for (int i = 0; i < frames; i++)
        {
            Assert.Equal(input[0][i] * 0.25f, output[0][i], 5);
            Assert.Equal(input[1][i] * 0.25f, output[1][i], 5);
        }
    }

    // ── 2. 状态往返：改参数 → 存 state → 再改 → 灌回去 → 声音回来 ──

    [Fact]
    public void RealVst3StateSurvivesTheRoundTrip()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start(Bundle("TestGain"), format: PluginFormats.Vst3);

        IReadOnlyList<ParameterInfo>? parameters = harness.Client.GetParametersBlocking(harness.Slot);
        Assert.NotNull(parameters);

        ParameterInfo gain = parameters!.First(p => p.Name == "Gain");

        Assert.True(harness.Client.SetParameterBlocking(harness.Slot, gain.Id, 0.25));

        const int frames = 256;
        var (input, output) = Buffers(frames);

        // 让 0.25 生效，再存一份 chunk。
        for (int block = 0; block < 2; block++)
            Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false));

        byte[]? saved = harness.Client.GetStateBlocking(harness.Slot);
        Assert.NotNull(saved);
        Assert.NotEmpty(saved!);

        // 换一个值：0.8。先让它生效。
        Assert.True(harness.Client.SetParameterBlocking(harness.Slot, gain.Id, 0.8));

        for (int block = 0; block < 2; block++)
            Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false));

        Assert.Equal(input[0][0] * 0.8f, output[0][0], 5);

        // 存回来的 state 灌回去：增益必须回到 0.25。
        Assert.True(harness.Client.SetStateBlocking(harness.Slot, saved!));

        for (int block = 0; block < 2; block++)
            Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false));

        Assert.Equal(input[0][0] * 0.25f, output[0][0], 5);
    }

    // ── 3. 真插件在 process 里崩掉：只死桥，宿主活着，并且能重新起一座干净的桥 ──

    [Fact]
    public void ARealPluginCrashingInProcessOnlyTakesTheBridgeDown()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start(Bundle("TestCrashOnProcess"), format: PluginFormats.Vst3);

        const int frames = 256;
        var (input, output) = Buffers(frames);

        // 第一块就把桥带走：调用失败，宿主进程毫发无损（能走到这一行就是证明）。
        Assert.False(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false));

        // 死桥上装不回来 —— 而且这一下必须把桥判死（VstBridgeClient 的 EOF 处理）。
        var onDeadBridge = harness.Client.LoadBlocking(harness.Slot, Bundle("TestGain"), 48000, 512,
            false, PluginFormats.Vst3);
        Assert.Null(onDeadBridge);
        Assert.False(harness.Client.IsRunning);

        // 重建一座桥，把好插件装进去，声音照旧。
        Assert.True(harness.Client.EnsureStartedBlocking(), harness.Client.LastBridgeError);

        var reload = harness.Client.LoadBlocking(harness.Slot, Bundle("TestGain"), 48000, 512,
            false, PluginFormats.Vst3);
        Assert.NotNull(reload);
        Assert.True(reload!.Ok, reload.Reason);

        Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false));
    }

    // ── 4. 真插件在 process 里卡死：音频路在一秒内判死并直通，宿主与桥都不陪葬 ──

    [Fact]
    public void ARealPluginHangingInProcessStallsTheAudioPathOnly()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start(Bundle("TestHangOnProcess"), format: PluginFormats.Vst3);

        const int frames = 256;
        var (input, output) = Buffers(frames);

        // 每块最多等 20ms 就放弃（音频线程不能被插件拖住），连续失败到阈值就判死。
        // 阈值是 50 次，留一倍余量。
        for (int block = 0; block < 200 && !harness.Client.AudioStalled; block++)
            harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false);

        Assert.True(harness.Client.AudioStalled, "the audio path did not stall within 200 blocks");

        // 卡死的桥还活着（宿主没把它杀掉），但关掉它必须是干净的、可重建的。
        harness.Client.KillBridge();
        Assert.False(harness.Client.IsRunning);

        Assert.True(harness.Client.EnsureStartedBlocking(), harness.Client.LastBridgeError);

        var reload = harness.Client.LoadBlocking(harness.Slot, Bundle("TestGain"), 48000, 512,
            false, PluginFormats.Vst3);
        Assert.NotNull(reload);
        Assert.True(reload!.Ok, reload.Reason);

        Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false));
    }

    // ── 5. 真插件在加载时崩掉：报失败（不是把宿主带走），桥能重建 ──

    [Fact]
    public void ARealPluginCrashingWhileLoadingIsReported()
    {
        if (!Ready())
            return;

        using var client = new VstBridgeClient(64);
        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        var load = client.LoadBlocking(1, Bundle("TestCrashOnLoad"), 48000, 512, false, PluginFormats.Vst3);

        Assert.Null(load);   // 桥死了，没有回执
        Assert.False(client.IsRunning);

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        var good = client.LoadBlocking(1, Bundle("TestGain"), 48000, 512, false, PluginFormats.Vst3);
        Assert.NotNull(good);
        Assert.True(good!.Ok, good.Reason);
    }

    // ── 6. 真插件在加载时卡死：加载超时会杀桥（约 20s），之后还能重建 ──

    [Fact]
    public void ARealPluginHangingWhileLoadingIsTimedOut()
    {
        if (!Ready())
            return;

        using var client = new VstBridgeClient(64);
        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        var stopwatch = Stopwatch.StartNew();
        var load = client.LoadBlocking(1, Bundle("TestHangOnLoad"), 48000, 512, false, PluginFormats.Vst3);
        stopwatch.Stop();

        mOutput.WriteLine($"loading a hanging plugin took {stopwatch.ElapsedMilliseconds} ms");

        Assert.Null(load);
        Assert.False(client.IsRunning);

        // 超时是"杀桥"，不是"等插件想通"：上限 20s，留 5s 余量。
        Assert.True(stopwatch.ElapsedMilliseconds < 25_000,
            $"the load timeout did not fire: {stopwatch.ElapsedMilliseconds} ms");

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        var good = client.LoadBlocking(1, Bundle("TestGain"), 48000, 512, false, PluginFormats.Vst3);
        Assert.NotNull(good);
        Assert.True(good!.Ok, good.Reason);
    }

    // ── 7. 卸载真插件：不挂、不崩，之后同一个槽位能装回来 ──

    [Fact]
    public void UnloadingARealVst3IsCleanAndTheSlotCanBeReused()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start(Bundle("TestGain"), format: PluginFormats.Vst3);

        Assert.True(harness.Client.UnloadBlocking(harness.Slot));

        var reload = harness.Client.LoadBlocking(harness.Slot, Bundle("TestGain"), 48000, 512,
            false, PluginFormats.Vst3);
        Assert.NotNull(reload);
        Assert.True(reload!.Ok, reload.Reason);
    }
}
