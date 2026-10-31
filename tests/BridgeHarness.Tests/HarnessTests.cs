using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace BridgeHarness.Tests;

// 这一组钉的全是"进程本身"：桥起来后是不是自成进程组、宿主没了桥会不会跟着死、
// 超时有没有真的把桥杀掉、残留有没有清掉。它们只有起真进程才测得到，
// 也只在 CI 的多平台上跑才有意义（本地只跑当前平台）。
public class HarnessTests
{
    readonly ITestOutputHelper mOutput;

    public HarnessTests(ITestOutputHelper output) => mOutput = output;

    bool Ready()
    {
        if (TestEnvironment.BridgeAvailable)
            return true;

        mOutput.WriteLine($"SKIP: no bridge executable at {TestEnvironment.BridgePath}");
        return false;
    }

    // ── 1. 最基本的：能起、能握手、自报两种格式 ──

    [Fact]
    public void StartsAndHandshakes()
    {
        if (!Ready())
            return;

        using var client = new VstBridgeClient(64);

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);
        Assert.True(client.IsRunning);
        Assert.NotNull(client.BridgeProcessIdForTest);

        string[] formats = client.BridgeFormats ?? [];

        Assert.Contains(PluginFormats.Vst2, formats);
        Assert.Contains(PluginFormats.Vst3, formats);
    }

    // ── 2. 音频真的穿过共享环（这是"端到端"的最小形态）──

    [Fact]
    public void GainIsAppliedThroughTheSharedRing()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start("gain:0.5");

        const int frames = 512;

        var input = new float[2][];
        var output = new float[2][];

        for (int c = 0; c < 2; c++)
        {
            input[c] = new float[frames];
            output[c] = new float[frames];
        }

        for (int i = 0; i < frames; i++)
        {
            input[0][i] = (float)Math.Sin(i * 0.01);
            input[1][i] = -input[0][i];
        }

        for (int block = 0; block < 32; block++)
        {
            Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false),
                "SendProcess failed on block " + block);

            for (int i = 0; i < frames; i++)
            {
                Assert.Equal(input[0][i] * 0.5f, output[0][i], 6);
                Assert.Equal(input[1][i] * 0.5f, output[1][i], 6);
            }
        }
    }

    [Fact]
    public void BypassPassesTheInputThrough()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start("gain:0");

        const int frames = 256;

        var input = new[] { new float[frames], new float[frames] };
        var output = new[] { new float[frames], new float[frames] };

        for (int i = 0; i < frames; i++)
        {
            input[0][i] = i / (float)frames;
            input[1][i] = 1 - i / (float)frames;
        }

        Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, true));

        for (int i = 0; i < frames; i++)
        {
            Assert.Equal(input[0][i], output[0][i], 6);
            Assert.Equal(input[1][i], output[1][i], 6);
        }
    }

    // 吞吐：不是性能门槛，只是"每块 20ms 的等待没有悄悄退化"。
    [Fact]
    public void ManyBlocksGoThroughWithoutStalling()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start("gain:1");

        const int frames = 512;
        const int blocks = 2000;

        var input = new[] { new float[frames], new float[frames] };
        var output = new[] { new float[frames], new float[frames] };

        for (int i = 0; i < frames; i++)
            input[0][i] = (float)Math.Sin(i * 0.001);

        input[1] = (float[])input[0].Clone();

        var stopwatch = Stopwatch.StartNew();

        for (int block = 0; block < blocks; block++)
            Assert.True(harness.Client.SendProcess(harness.Slot, input, output, 2, frames, false),
                "SendProcess failed on block " + block);

        stopwatch.Stop();

        double perSecond = blocks / stopwatch.Elapsed.TotalSeconds;
        mOutput.WriteLine($"{blocks} blocks of {frames} frames in {stopwatch.ElapsedMilliseconds} ms " +
                          $"= {perSecond:0} blocks/s");

        // 实时是 93.75 块/秒（48 kHz / 512）。留一个很宽的余量，只拦住"卡死"。
        Assert.True(perSecond > 100, $"audio path is too slow: {perSecond:0} blocks/s");
    }

    // ── 3. 状态 / 参数（同一根管道上的低频往返）──

    [Fact]
    public void StateAndParametersRoundTrip()
    {
        if (!Ready())
            return;

        using var harness = Harness.Start("gain:0.5");

        byte[]? state = harness.Client.GetStateBlocking(harness.Slot);
        Assert.NotNull(state);
        Assert.Equal(0.5, BitConverter.ToDouble(state!), 6);

        Assert.True(harness.Client.SetStateBlocking(harness.Slot, BitConverter.GetBytes(0.25)));

        state = harness.Client.GetStateBlocking(harness.Slot);
        Assert.NotNull(state);
        Assert.Equal(0.25, BitConverter.ToDouble(state!), 6);

        var parameters = harness.Client.GetParametersBlocking(harness.Slot);
        Assert.NotNull(parameters);
        Assert.Single(parameters!);
        Assert.Equal(0.25, parameters![0].Value, 6);

        Assert.True(harness.Client.SetParameterBlocking(harness.Slot, 1, 0.75));

        parameters = harness.Client.GetParametersBlocking(harness.Slot);
        Assert.NotNull(parameters);
        Assert.Equal(0.75, parameters![0].Value, 6);
    }

    // ── 4. 宿主暴毙（不是正常 Dispose）→ 桥必须跟着死，且不留孙进程 ──

    [Fact]
    public void KillingTheHostTakesTheBridgeAndItsGrandchildDown()
    {
        if (!Ready())
            return;

        if (!TestEnvironment.HostRunnerAvailable)
        {
            mOutput.WriteLine("SKIP: HostRunner was not staged");
            return;
        }

        (List<string> lines, Process host) = StartHost("child");

        try
        {
            int bridgePid = PidOf(lines, "BRIDGE");

            string load = lines.First(l => l.StartsWith("LOAD ", StringComparison.Ordinal))["LOAD ".Length..];
            Assert.StartsWith("child-", load);
            int grandchildPid = int.Parse(load["child-".Length..], CultureInfo.InvariantCulture);

            mOutput.WriteLine($"host={host.Id} bridge={bridgePid} grandchild={grandchildPid}");

            Assert.True(TestEnvironment.ProcessExists(bridgePid), "the bridge never started");
            Assert.True(TestEnvironment.ProcessExists(grandchildPid), "the grandchild never started");

            // 这就是"任务管理器结束进程"：宿主没机会 Dispose、没机会关句柄。
            host.Kill();
            host.WaitForExit(10_000);

            TestEnvironment.WaitUntil(() => !TestEnvironment.ProcessExists(bridgePid), 15_000,
                "the bridge to die with its host");

            TestEnvironment.WaitUntil(() => !TestEnvironment.ProcessExists(grandchildPid), 15_000,
                "the grandchild to die with its host");
        }
        finally
        {
            if (!host.HasExited)
                host.Kill();
        }
    }

    static (List<string> Lines, Process Host) StartHost(string plugin)
    {
        var start = new ProcessStartInfo(TestEnvironment.HostRunnerPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add(plugin);

        var host = Process.Start(start) ?? throw new InvalidOperationException("cannot start HostRunner");

        var lines = new List<string>();

        for (int i = 0; i < 120; i++)
        {
            var read = host.StandardOutput.ReadLineAsync();

            if (!read.Wait(TimeSpan.FromSeconds(5)))
                break;

            string? line = read.Result;

            if (line == null)
                break;

            lines.Add(line);

            if (line == "READY")
                return (lines, host);
        }

        string stderr = host.HasExited ? host.StandardError.ReadToEnd() : "(still running)";

        throw new XunitException("HostRunner never became ready. stdout: " + string.Join(" | ", lines) +
                                 " stderr: " + stderr);
    }

    static int PidOf(List<string> lines, string key)
    {
        string? line = lines.FirstOrDefault(l => l.StartsWith(key + " ", StringComparison.Ordinal));

        if (line == null)
            throw new XunitException($"HostRunner did not print '{key}'");

        return int.Parse(line[(key.Length + 1)..], CultureInfo.InvariantCulture);
    }

    // ── 5. 只关存活管道（等价于宿主被内核回收写端）→ 桥的看门狗自杀 ──

    [Fact]
    public void LivenessEofKillsTheBridge()
    {
        if (!Ready())
            return;

        if (TestEnvironment.IsUnix == false)
        {
            mOutput.WriteLine("SKIP: Windows uses a JobObject, not the liveness pipe");
            return;
        }

        using var harness = Harness.Start("gain:1");

        int bridgePid = harness.Client.BridgeProcessIdForTest!.Value;

        harness.Client.CloseLivenessForTest();

        TestEnvironment.WaitUntil(() => !TestEnvironment.ProcessExists(bridgePid), 10_000,
            "the bridge to die on liveness EOF");
    }

    // ── 6. Unix：桥要自成进程组（否则宿主没法"一锅端"）──

    [Fact]
    public void TheBridgeRunsInItsOwnProcessGroup()
    {
        if (!Ready())
            return;

        if (TestEnvironment.IsUnix == false)
        {
            mOutput.WriteLine("SKIP: process groups are a Unix concept");
            return;
        }

        using var harness = Harness.Start("gain:1");

        int bridgePid = harness.Client.BridgeProcessIdForTest!.Value;

        int bridgeGroup = TestEnvironment.ReadProcessGroup(bridgePid);
        int ourGroup = TestEnvironment.ReadProcessGroup(Environment.ProcessId);

        mOutput.WriteLine($"bridge pid={bridgePid} pgid={bridgeGroup}; test pid={Environment.ProcessId} pgid={ourGroup}");

        Assert.Equal(bridgePid, bridgeGroup);
        Assert.NotEqual(ourGroup, bridgeGroup);
    }

    // ── 7. 主动关桥 → 进程真的没了；再起一座新的还能用 ──

    [Fact]
    public void KillBridgeIsFinalAndRestartWorks()
    {
        if (!Ready())
            return;

        using var client = new VstBridgeClient(64);

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);
        Assert.True(client.LoadBlocking(1, "gain:0.75", 48000, 512, false) is { Ok: true });

        int bridgePid = client.BridgeProcessIdForTest!.Value;

        client.KillBridge();

        TestEnvironment.WaitUntil(() => !TestEnvironment.ProcessExists(bridgePid), 10_000,
            "KillBridge to remove the bridge process");

        Assert.False(client.IsRunning);

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        var reloaded = client.LoadBlocking(1, "gain:0.75", 48000, 512, false);

        Assert.NotNull(reloaded);
        Assert.True(reloaded!.Ok, reloaded.Reason);
    }

    // ── 8. 插件把桥搞崩 → 宿主不陪葬，下一次操作会起干净的桥 ──

    [Fact]
    public void APluginThatCrashesTheBridgeDoesNotTakeTheHostDown()
    {
        if (!Ready())
            return;

        using var client = new VstBridgeClient(64);

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        var crashed = client.LoadBlocking(1, "crash", 48000, 512, false);

        Assert.Null(crashed);

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        var reloaded = client.LoadBlocking(2, "gain:0.5", 48000, 512, false);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.Ok, reloaded.Reason);
    }

    // ── 9. 插件在 Load 里永远卡住 → 宿主 20s 超时 → 杀桥；不能留下永久的坏客户端 ──

    [Fact]
    public void AHungPluginIsTimedOutAndTheBridgeIsKilled()
    {
        if (!Ready())
            return;

        using var client = new VstBridgeClient(64);

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        int bridgePid = client.BridgeProcessIdForTest!.Value;

        var stopwatch = Stopwatch.StartNew();
        var result = client.LoadBlocking(1, "hang", 48000, 512, false);
        stopwatch.Stop();

        mOutput.WriteLine($"load returned after {stopwatch.Elapsed.TotalSeconds:0.0}s: {client.LastTimeoutError}");

        Assert.Null(result);
        Assert.False(string.IsNullOrEmpty(client.LastTimeoutError));
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 15, 60);

        TestEnvironment.WaitUntil(() => !TestEnvironment.ProcessExists(bridgePid), 10_000,
            "the hung bridge to be killed");

        Assert.False(client.IsRunning);

        // 超时杀桥之后，客户端必须还能用 —— 否则插件卡一次就把整个混音台拖死。
        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        var reloaded = client.LoadBlocking(2, "gain:0.25", 48000, 512, false);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.Ok, reloaded.Reason);
    }

    // ── 10. Unix：上一轮被 SIGKILL 留下的 socket 不能挡住重启 ──

    [Fact]
    public void AStalePipeSocketDoesNotBlockARestart()
    {
        if (!Ready())
            return;

        if (TestEnvironment.IsUnix == false)
        {
            mOutput.WriteLine("SKIP: Windows uses named pipes, not filesystem sockets");
            return;
        }

        using var client = new VstBridgeClient(64);

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);
        client.KillBridge();

        string pipeName = BridgeProtocol.PipeName + "-64-" + Environment.ProcessId;
        string socket = BridgeResidue.PipeSocketPath(pipeName);

        // 模拟"桥被 SIGKILL，没来得及 unlink 自己的 UDS socket"。
        File.WriteAllBytes(socket, []);
        Assert.True(File.Exists(socket));

        Assert.True(client.EnsureStartedBlocking(), client.LastBridgeError);

        Assert.True(client.LoadBlocking(1, "gain:1", 48000, 512, false) is { Ok: true });
    }

    // ── 11. Unix：强杀留下的段文件 / socket，按"宿主 PID 已死"清掉 ──

    [Fact]
    public void ResidueSweepRemovesFilesOfDeadHostsAndKeepsLiveOnes()
    {
        if (!Ready())
            return;

        if (TestEnvironment.IsUnix == false)
        {
            mOutput.WriteLine("SKIP: Windows reclaims these with the process");
            return;
        }

        string directory = SharedAudioRing.UnixSectionDirectory;
        Directory.CreateDirectory(directory);

        // int.MaxValue 一定不是活着的进程：两份残骸都应该被清掉。
        string deadPrefix = BridgeProtocol.PipeName + "-64-" + int.MaxValue;

        string deadSection = Path.Combine(directory, deadPrefix + ".in");
        string deadSocket = BridgeResidue.PipeSocketPath(deadPrefix);

        File.WriteAllBytes(deadSection, [1, 2, 3]);
        File.WriteAllBytes(deadSocket, []);

        // 反例：名字里的 PID 是我们自己（活人），绝不能删。
        string alivePrefix = BridgeProtocol.PipeName + "-64-" + Environment.ProcessId;
        string aliveSection = Path.Combine(directory, alivePrefix + ".out");

        File.WriteAllBytes(aliveSection, []);

        try
        {
            BridgeResidue.Sweep();

            Assert.False(File.Exists(deadSection), "residue sweep kept a dead host's section file");
            Assert.False(File.Exists(deadSocket), "residue sweep kept a dead host's pipe socket");
            Assert.True(File.Exists(aliveSection), "residue sweep deleted a live host's section file");
        }
        finally
        {
            TryDelete(aliveSection);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
