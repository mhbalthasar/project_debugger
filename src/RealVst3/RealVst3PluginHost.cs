using System;
using System.Collections.Generic;
using System.IO;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.ExperimentalPatch.VstBridge;

// [debug-harness] 桥进程里的**真** VST3 宿主。
//
// 逐字复制自正式工程 ExperimentalPatch/VstBridge/Vst3PluginHost.cs，唯一删掉的是
// P2 的编辑器那条路（Vst3EditorWindow：起 STA 线程、把插件窗口的 HWND 回传宿主）。
// 删它的理由：编辑器窗口是**宿主进程的 GUI**，而验证台没有 GUI，P2 的窗口泵在
// macOS / Linux 上也还没落地（VST3-BRIDGE.md §9 把它划在 P5 之外）。加载 / 处理 /
// 状态 / 参数 / 侧链 / 卸载这些"音频侧"的代码保持原样 —— 那才是本验证台要证明的部分。
//
// 【为什么不用假的】FakePluginHost 证明的是"桥的管道与共享环能把样本搬过去"；
// 这一格证明的是"真的 OwnVST3 native 在 Linux / macOS 上能把真的 .vst3 装进桥、
// 出声、存取状态，插件炸了只死桥"。
internal sealed class RealVst3PluginHost : IBridgePluginHost
{
    static readonly string? sNativePath = FindNativeLibrary();

    // 这个桥能不能承载真 VST3（native 就位才是 true，与正式工程同一条判据）。
    public static bool IsSupported => sNativePath != null;

    readonly Dictionary<int, Slot> mSlots = [];
    SharedAudioRing? mIn;
    SharedAudioRing? mOut;
    Vst3Backend? mBackend;

    static string? FindNativeLibrary()
    {
        if (!Environment.Is64BitProcess)
            return null;

        string beside = Path.Combine(AppContext.BaseDirectory, Vst3Backend.NativeFileName);
        return File.Exists(beside) ? beside : null;
    }

    public void Attach(SharedAudioRing input, SharedAudioRing output)
    {
        mIn = input;
        mOut = output;
    }

    public (bool Ok, string? Reason, string? Name, string? Vendor, int LatencySamples, bool IsSynth,
        bool HasEditor, bool NeedsOpenGl) Load(
        LoadPluginMessage request, out int inputChannels, out int outputChannels)
    {
        inputChannels = 0;
        outputChannels = 0;

        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        if (!IsSupported)
            return (false, "the VST3 host library is missing in this bridge", null, null, 0, false, false, false);

        // [debug-harness finding 4] 原实现是 File.Exists(request.Path)：.vst3 在三个平台上
        // 都是**目录包**（Contents/... 里才是二进制），File.Exists 对目录恒为 false —— 于是
        // 隔离模式下每个真 VST3 都会在"插件文件不存在"上失败，而进程内那条路（Vst3Backend
        // 直接把 Locator 交给 native）根本不做这个检查，所以一直没暴露。应当回灌 TuneLab。
        if (!File.Exists(request.Path) && !Directory.Exists(request.Path))
            return (false, "plugin file not found", null, null, 0, false, false, false);

        mBackend ??= new Vst3Backend();
        if (!mBackend.IsAvailable)
            return (false, mBackend.UnavailableReason ?? "the VST3 host library is missing", null, null, 0, false, false, false);

        var info = new InsertPluginInfo
        {
            Format = "VST3",
            PluginId = "vst3:" + Path.GetFileNameWithoutExtension(request.Path),
            Name = Path.GetFileNameWithoutExtension(request.Path),
            Locator = request.Path,
        };

        // 块长不能超过共享环单块容量：超了 TryPublish 直接失败、那一块白直通。
        int block = Math.Clamp(request.MaxBlock, 32, SharedAudioLayout.MaxBlockFrames);

        var settings = new InsertSettings
        {
            Bypass = request.Bypass,
            SampleRate = request.SampleRate > 0 ? request.SampleRate : 48000,
            MaxBlockSize = block,
        };

        Vst3Insert insert;
        try
        {
            if (mBackend.Create(info, settings, out string? reason) is not Vst3Insert created)
                return (false, reason ?? "the plugin could not be created", null, null, 0, false, false, false);

            insert = created;
        }
        catch (Exception ex)
        {
            return (false, "unexpected error while loading the plugin: " + ex.Message, null, null, 0, false, false, false);
        }

        var slot = new Slot
        {
            Insert = insert,
            Frames = block,
            Bypassed = request.Bypass,
            Interleaved = new float[block * 2],
            Sidechain = new float[block * 2],
            PublishPlanes = new float[2][],
        };

        for (int c = 0; c < 2; c++)
            slot.PublishPlanes[c] = new float[block];

        mSlots[request.Slot] = slot;

        inputChannels = insert.InputChannels;
        outputChannels = 2;

        bool hasEditor;
        bool needsOpenGl;
        try
        {
            hasEditor = insert.HasEditor;
            needsOpenGl = insert.NeedsOpenGl;
        }
        catch (Exception)
        {
            hasEditor = false;
            needsOpenGl = false;
        }

        return (true, null, insert.Name, insert.Vendor, insert.LatencySamples, false, hasEditor, needsOpenGl);
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        try
        {
            slot.Insert.Dispose();
        }
        catch (Exception)
        {
            // 卸载阶段插件抛错：句柄马上要没，吞掉。
        }

        return true;
    }

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
            if (bypass || slot.Bypassed)
            {
                mOut?.TryPublish(slotNumber, inPlanes, ch, fr, 1000);
                return true;
            }

            int n = Math.Min(fr, slot.Frames);

            // 平面 → 交织（Vst3Insert 的 Process 要的是交织立体声）。
            var interleaved = slot.Interleaved;
            float[] left = inPlanes[0];
            float[] right = inPlanes.Length > 1 ? inPlanes[1] : inPlanes[0];
            for (int i = 0; i < n; i++)
            {
                interleaved[i * 2] = left[i];
                interleaved[i * 2 + 1] = right[i];
            }

            // 侧链：第 3/4 条平面重建成交织键，交给 Vst3Insert。
            ReadOnlySpan<float> sidechain = default;
            if (ch > 2 && slot.Insert.SidechainChannels > 0)
            {
                var sc = slot.Sidechain;
                float[] scL = ch > 2 && inPlanes.Length > 2 ? inPlanes[2] : inPlanes[0];
                float[] scR = ch > 3 && inPlanes.Length > 3 ? inPlanes[3] : scL;
                for (int i = 0; i < n; i++)
                {
                    sc[i * 2] = scL[i];
                    sc[i * 2 + 1] = scR[i];
                }

                sidechain = new ReadOnlySpan<float>(sc, 0, n * 2);
            }

            bool ok;
            try
            {
                slot.Insert.Process(new Span<float>(interleaved, 0, n * 2), 0, n, sidechain);
                ok = true;
            }
            catch (Exception)
            {
                ok = false;
            }

            if (!ok)
            {
                // 插件炸了：这一块直通，用户至少还听得见原来的东西。
                mOut?.TryPublish(slotNumber, inPlanes, ch, fr, 1000);
                return false;
            }

            // 交织 → 平面（VST3 输出固定立体声；NaN/Inf 就地清零）。
            for (int i = 0; i < n; i++)
            {
                float lo = interleaved[i * 2];
                float ro = interleaved[i * 2 + 1];
                slot.PublishPlanes[0][i] = float.IsFinite(lo) ? lo : 0f;
                slot.PublishPlanes[1][i] = float.IsFinite(ro) ? ro : 0f;
            }

            mOut?.TryPublish(slotNumber, slot.PublishPlanes, 2, n, 1000);
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
        => mSlots.TryGetValue(slotNumber, out var slot) ? slot.Insert.SaveState() : null;

    public bool SetState(int slotNumber, string base64)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        byte[]? data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        return slot.Insert.LoadState(data);
    }

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        var list = new List<ParameterInfo>();

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return list;

        try
        {
            foreach (var parameter in slot.Insert.Parameters)
            {
                list.Add(new ParameterInfo
                {
                    Id = parameter.Id,
                    Name = parameter.Name ?? string.Empty,
                    Min = parameter.MinValue,
                    Max = parameter.MaxValue,
                    Value = parameter.CurrentValue,
                    DefaultValue = parameter.DefaultValue,
                });
            }
        }
        catch (Exception)
        {
            list.Clear();
        }

        return list;
    }

    public void SetParameter(int slotNumber, long id, double value)
    {
        if (mSlots.TryGetValue(slotNumber, out var slot))
            slot.Insert.SetParameter((int)id, value);
    }

    // [debug-harness] 没有编辑器：验证台没有 GUI，P2 的窗口泵也不在 P5 范围内。
    // 宿主侧拿到 false 就退参数面板 —— 与"插件在 macOS/Linux 上还没接 UI"是同一个降级路径。
    public (bool Ok, string? Reason, int ChildWindows, IntPtr Window) OpenEditor(int slotNumber, string title)
        => (false, "the debug harness has no GUI", 0, IntPtr.Zero);

    public bool CloseEditor(int slotNumber) => true;

    sealed class Slot
    {
        public Vst3Insert Insert = null!;

        public int Frames;
        public bool Bypassed;

        // 每块搬运用的预分配缓冲（不随块分配）。
        public float[] Interleaved = [];
        public float[] Sidechain = [];

        // 发布给宿主的 2 条平面视图。
        public float[][] PublishPlanes = [];
    }
}
