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
}
