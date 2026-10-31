using System;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;

// 一个"真宿主"进程：起桥、加载假插件、把 PID 打到 stdout，然后什么都不做。
//
// 测试进程随后把这个宿主 SIGKILL / TerminateProcess 掉 —— 这是"用户强杀 TuneLab"
// 的等价物，比"只关掉存活管道"强：它连宿主自己的 Dispose 也一起跳过了。
internal static class HostRunner
{
    static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "hangprobe")
            return HangProbe(args[1]);

        string plugin = args.Length > 0 ? args[0] : "gain:1";

        var client = new VstBridgeClient(64);

        if (!client.EnsureStartedBlocking())
        {
            Console.Error.WriteLine("ensure-started failed: " + client.LastBridgeError);
            return 3;
        }

        var load = client.LoadBlocking(1, plugin, 48000, 512, false);

        if (load == null || !load.Ok)
        {
            Console.Error.WriteLine("load failed: " + (load?.Reason ?? client.LastBridgeError));
            return 4;
        }

        Console.WriteLine("BRIDGE " + (client.BridgeProcessIdForTest?.ToString() ?? "-1"));
        Console.WriteLine("LOAD " + load.Name);
        Console.WriteLine("READY");
        Console.Out.Flush();

        // 故意不返回：让测试有机会在"宿主正常退出"之外的方式下杀掉我们。
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    // [debug-harness] 现场诊断用：把"卡在哪一步"打到 stderr。
    // 单独一个看门狗线程每 2s 打印一次当前步骤 —— 主线程被卡住时它仍然醒着。
    static volatile int sStep;
    static volatile bool sDone;

    static void Mark(int step, string what)
    {
        sStep = step;
        Console.Error.WriteLine($"[probe] {DateTime.Now:HH:mm:ss.fff} step {step}: {what}");
    }

    static int HangProbe(string bundle)
    {
        var watchdog = new Thread(() =>
        {
            while (!sDone)
            {
                Thread.Sleep(2000);
                if (!sDone)
                    Console.Error.WriteLine($"[probe] {DateTime.Now:HH:mm:ss.fff} WATCHDOG still in step {sStep}");
            }
        }) { IsBackground = true };
        watchdog.Start();

        var client = new VstBridgeClient(64);

        Mark(1, "ensure started");
        if (!client.EnsureStartedBlocking())
        {
            Console.Error.WriteLine("ensure-started failed: " + client.LastBridgeError);
            sDone = true;
            return 3;
        }

        Mark(2, "load " + bundle);
        var load = client.LoadBlocking(1, bundle, 48000, 512, false, PluginFormats.Vst3);
        Console.Error.WriteLine($"[probe] loaded: ok={load?.Ok} name={load?.Name} reason={load?.Reason}");
        if (load == null || !load.Ok)
        {
            sDone = true;
            return 4;
        }

        var input = new[] { new float[256], new float[256] };
        var output = new[] { new float[256], new float[256] };

        for (int i = 0; i < 200 && !client.AudioStalled; i++)
        {
            Mark(3, $"process block {i} (stalled={client.AudioStalled})");
            bool ok = client.SendProcess(1, input, output, 2, 256, false);
            if (i < 5 || i % 50 == 0)
                Console.Error.WriteLine($"[probe] block {i}: {ok}");
        }

        Console.Error.WriteLine($"[probe] stalled={client.AudioStalled} running={client.IsRunning}");

        Mark(4, "kill bridge");
        client.KillBridge();
        Console.Error.WriteLine($"[probe] after kill: running={client.IsRunning}");

        Mark(5, "ensure started again");
        Console.Error.WriteLine($"[probe] restarted: {client.EnsureStartedBlocking()} {client.LastBridgeError}");

        Mark(6, "load good plugin");
        var good = client.LoadBlocking(1, bundle, 48000, 512, false, PluginFormats.Vst3);
        Console.Error.WriteLine($"[probe] reload: ok={good?.Ok} reason={good?.Reason}");

        Mark(7, "process good plugin");
        Console.Error.WriteLine($"[probe] process: {client.SendProcess(1, input, output, 2, 256, false)}");

        sDone = true;
        return 0;
    }
}
