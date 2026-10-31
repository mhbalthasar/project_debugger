using System;
using TuneLab.ExperimentalPatch.InsertHost;

// 一次"起桥 + 加载假插件"的完整组合，用完就整座杀干净。
internal sealed class Harness : IDisposable
{
    public VstBridgeClient Client { get; }

    public int Slot { get; }

    public string? PluginName { get; }

    Harness(VstBridgeClient client, int slot, string? pluginName)
    {
        Client = client;
        Slot = slot;
        PluginName = pluginName;
    }

    public static Harness Start(string pluginPath, int slot = 1)
    {
        var client = new VstBridgeClient(64);

        if (!client.EnsureStartedBlocking())
        {
            string error = client.LastBridgeError ?? "(no error)";
            client.Dispose();
            throw new InvalidOperationException("the bridge did not start: " + error);
        }

        var load = client.LoadBlocking(slot, pluginPath, 48000, 512, false);

        if (load == null || !load.Ok)
        {
            string error = load?.Reason ?? client.LastBridgeError ?? "(no error)";
            client.Dispose();
            throw new InvalidOperationException("loading the fake plugin failed: " + error);
        }

        return new Harness(client, slot, load.Name);
    }

    public void Dispose() => Client.Dispose();
}
