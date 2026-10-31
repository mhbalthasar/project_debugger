using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using TuneLab.ExperimentalPatch.VstBridge;

// [debug-harness] 桥进程里的"假插件"。
//
// 正式工程里这一格是 PluginHost（VST2 AEffect ABI）与 Vst3PluginHost（ownvst3）。
// 验证隔离机制时不需要真的 native 代码，需要的只是"一个能被加载、能处理音频、
// 能被要求卡死 / 崩溃 / 拉起孙进程的宿主实现"。协议、帧读写、槽位路由、
// 共享音频环、进程生命周期全部是正式代码，只有这里被替换。
//
// 用插件路径当指令（LoadPluginMessage.Path 本来就只是一个字符串）：
//   gain:<x>  每块乘以 x
//   hang      Load 里永远睡下去 —— 用来验证宿主 20s 加载超时会杀桥
//   crash     Load 里 Environment.Exit —— 用来验证桥暴毙后宿主能重启
//   child     拉起一个长命子进程，名字里回它的 PID —— 用来验证不留孤儿
//   其它      与 gain:1 等价
internal sealed class FakePluginHost : IBridgePluginHost
{
    sealed class Slot
    {
        public double Gain = 1;
        public int ChildPid;

        // 与真 PluginHost 一样预分配：音频路径上不分配。
        public readonly float[][] OutPlanes = new float[SharedAudioLayout.MaxChannels][];

        public Slot()
        {
            for (int c = 0; c < OutPlanes.Length; c++)
                OutPlanes[c] = new float[SharedAudioLayout.MaxBlockFrames];
        }
    }

    const long GainParameterId = 1;

    readonly Dictionary<int, Slot> mSlots = [];

    SharedAudioRing? mIn;
    SharedAudioRing? mOut;

    public void Attach(SharedAudioRing input, SharedAudioRing output)
    {
        mIn = input;
        mOut = output;
    }

    public (bool Ok, string? Reason, string? Name, string? Vendor, int LatencySamples, bool IsSynth,
        bool HasEditor, bool NeedsOpenGl) Load(LoadPluginMessage request, out int inputChannels, out int outputChannels)
    {
        inputChannels = 2;
        outputChannels = 2;

        string path = request.Path ?? string.Empty;

        // 这两个是"插件把桥搞死"的两种典型方式，必须真的发生在桥进程里。
        if (path == "hang")
            Thread.Sleep(Timeout.Infinite);

        if (path == "crash")
        {
            Console.Error.WriteLine("fake-plugin: crashing the bridge on purpose");
            Environment.Exit(7);
        }

        var slot = new Slot();

        if (path.StartsWith("gain:", StringComparison.Ordinal)
            && double.TryParse(path[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out double gain))
        {
            slot.Gain = gain;
        }

        if (path == "child")
            slot.ChildPid = SpawnChild();

        mSlots[request.Slot] = slot;

        return (true, null,
            slot.ChildPid > 0 ? "child-" + slot.ChildPid : "fake-gain",
            "debug-harness", 0, false, false, false);
    }

    // 孙进程：桥被 SIGKILL / 进程组被打掉 / 作业关闭时必须一起走。
    static int SpawnChild()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "127.0.0.1 -n 300")
            : new ProcessStartInfo("/bin/sleep", "300");

        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        return System.Diagnostics.Process.Start(start)?.Id ?? 0;
    }

    public bool Unload(int slotNumber) => mSlots.Remove(slotNumber);

    public bool Process(int slotNumber, bool bypass, out int frames, out int channels)
    {
        frames = 0;
        channels = 0;

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        if (mIn == null || !mIn.TryRead(0, out _, out var inPlanes, out int ch, out int fr))
            return false;

        frames = fr;
        channels = ch;

        try
        {
            if (bypass)
            {
                mOut?.TryPublish(slotNumber, inPlanes, ch, fr, 1000);
                return true;
            }

            for (int c = 0; c < Math.Min(ch, slot.OutPlanes.Length); c++)
            {
                float[] source = inPlanes[Math.Min(c, inPlanes.Length - 1)];
                float[] destination = slot.OutPlanes[c];

                for (int i = 0; i < fr; i++)
                    destination[i] = (float)(source[i] * slot.Gain);
            }

            mOut?.TryPublish(slotNumber, slot.OutPlanes, ch, fr, 1000);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            mIn.Release();
        }
    }

    public byte[]? GetState(int slotNumber)
        => mSlots.TryGetValue(slotNumber, out var slot) ? BitConverter.GetBytes(slot.Gain) : null;

    public bool SetState(int slotNumber, string base64)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        try
        {
            byte[] bytes = Convert.FromBase64String(base64);

            if (bytes.Length != sizeof(double))
                return false;

            slot.Gain = BitConverter.ToDouble(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return [];

        return
        [
            new ParameterInfo
            {
                Id = GainParameterId,
                Name = "Gain",
                Min = 0,
                Max = 2,
                Value = slot.Gain,
            },
        ];
    }

    public void SetParameter(int slotNumber, long id, double value)
    {
        if (id == GainParameterId && mSlots.TryGetValue(slotNumber, out var slot))
            slot.Gain = value;
    }

    public (bool Ok, string? Reason, int ChildWindows, IntPtr Window) OpenEditor(int slotNumber, string title)
        => (false, "the debug harness has no editor", 0, IntPtr.Zero);

    public bool CloseEditor(int slotNumber) => true;
}
