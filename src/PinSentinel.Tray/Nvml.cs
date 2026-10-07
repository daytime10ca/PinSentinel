using System.Runtime.InteropServices;

namespace PinSentinel.Tray;

sealed record GpuStats(uint TempC, uint LoadPercent, double VramUsedGb, double VramTotalGb, uint FanPercent, double BoardWatts);

/// <summary>General GPU telemetry from the NVIDIA management library. Read-only.</summary>
static class Nvml
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu, Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory { public ulong Total, Free, Used; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    private static IntPtr s_device;
    private static bool s_failed;

    public static GpuStats? Read()
    {
        if (s_failed) return null;
        try
        {
            if (s_device == IntPtr.Zero && (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out s_device) != 0))
            {
                s_failed = true;
                return null;
            }

            nvmlDeviceGetTemperature(s_device, 0, out uint temp);
            nvmlDeviceGetUtilizationRates(s_device, out var load);
            nvmlDeviceGetMemoryInfo(s_device, out var memory);
            nvmlDeviceGetFanSpeed(s_device, out uint fan);
            nvmlDeviceGetPowerUsage(s_device, out uint milliwatts);
            const double Gb = 1024.0 * 1024 * 1024;
            return new GpuStats(temp, load.Gpu, memory.Used / Gb, memory.Total / Gb, fan, milliwatts / 1000.0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_failed = true;
            return null;
        }
    }
}
