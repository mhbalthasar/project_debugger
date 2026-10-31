using System;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;

// 一个"真宿主"进程：起桥、加载假插件、把 PID 打到 stdout，然后什么都不做。
//
// 测试进程随后把这个宿主 SIGKILL / TerminateProcess 掉 —— 这是"用户强杀 TuneLab"
// 的等价物，比"只关掉存活管道"强：它连宿主自己的 Dispose 也一起跳过了。
internal static class HostRunner
{
    static int Main(string[] args)
    {
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
}
