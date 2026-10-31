using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit.Sdk;

internal static class TestEnvironment
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    // 与 VstBridgeClient.BridgeRid 同一套算法。
    public static string Rid
    {
        get
        {
            bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

            if (OperatingSystem.IsWindows())
                return arm ? "win-arm64" : "win-x64";

            if (OperatingSystem.IsMacOS())
                return arm ? "osx-arm64" : "osx-x64";

            return arm ? "linux-arm64" : "linux-x64";
        }
    }

    public static string BridgeExecutableName =>
        OperatingSystem.IsWindows()
            ? "TuneLab.ExperimentalPatch.VstBridge.x64.exe"
            : "TuneLab.ExperimentalPatch.VstBridge.x64";

    public static string BridgePath =>
        Path.Combine(AppContext.BaseDirectory, "bridge", Rid, BridgeExecutableName);

    public static bool BridgeAvailable => File.Exists(BridgePath);

    public static string HostRunnerPath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "hostrunner",
            OperatingSystem.IsWindows() ? "HostRunner.exe" : "HostRunner");

    public static bool HostRunnerAvailable => File.Exists(HostRunnerPath);

    public static void WaitUntil(Func<bool> condition, int timeoutMs, string what)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return;

            Thread.Sleep(50);
        }

        if (!condition())
            throw new XunitException($"{what} did not happen within {timeoutMs} ms");
    }

    public static bool ProcessExists(int pid)
    {
        if (pid <= 0)
            return false;

        if (IsUnix)
            return UnixProcess.IsAlive(pid);

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // 进程组 ID（Unix）。ps 在 Linux 与 macOS 上都认 -o pgid= -p <pid>。
    public static int ReadProcessGroup(int pid)
    {
        var start = new ProcessStartInfo("ps")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("pgid=");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot run ps");

        string text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(5000);

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pgid))
            throw new InvalidOperationException($"ps returned '{text}' for pid {pid}");

        return pgid;
    }
}
