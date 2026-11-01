using System;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;

// 一次"起桥 + 加载插件"的完整组合，用完就整座杀干净。
internal sealed class Harness : IDisposable
{
    public VstBridgeClient Client { get; }

    public int Slot { get; }

    public string? PluginName { get; }

    // 桥自报的插件名 / 厂商 / 延迟（真 VST3 那组要断言）。
    public LoadPluginResultMessage Load { get; }

    Harness(VstBridgeClient client, int slot, LoadPluginResultMessage load)
    {
        Client = client;
        Slot = slot;
        Load = load;
        PluginName = load.Name;
    }

    public static Harness Start(string pluginPath, int slot = 1, string format = PluginFormats.Vst2)
    {
        var client = new VstBridgeClient(64);

        if (!client.EnsureStartedBlocking())
        {
            string error = client.LastBridgeError ?? "(no error)";
            client.Dispose();
            throw new InvalidOperationException("the bridge did not start: " + error);
        }

        var load = client.LoadBlocking(slot, pluginPath, 48000, 512, false, format);

        if (load == null || !load.Ok)
        {
            string error = load?.Reason ?? client.LastBridgeError ?? "(no error)";
            client.Dispose();
            throw new InvalidOperationException("loading the plugin failed: " + error);
        }

        return new Harness(client, slot, load);
    }

    public void Dispose() => Client.Dispose();

    // 送一块立体声音频，**允许过载一次**。
    //
    // 产品的实时纪律本来就是"20 ms 内没赶上就直通"，而不是"这一块必须成功"；共享 CI runner
    // 上的调度抖动正好落在这条缝里（osx-x64 实测偶发丢一块，本机 20/20）。所以统一从这里送：
    // 重试一次再判失败，既不放松"必须出声"的断言，也不把噪声当缺陷。
    // 需要断言"这一块**必须**失败"的用例（杀桥 / 卡死）继续直接用 Client.SendProcess。
    public bool SendBlock(float[][] input, float[][] output, int frames)
        => SendBlockOnce(input, output, frames) || SendBlockOnce(input, output, frames);

    public bool SendBlockOnce(float[][] input, float[][] output, int frames)
        => Client.SendProcess(Slot, input, output, 2, frames, false);
}
