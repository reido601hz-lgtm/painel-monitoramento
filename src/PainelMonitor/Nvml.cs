using System.Runtime.InteropServices;
using System.Text;

namespace PainelMonitor;

/// <summary>Leitura minima da NVIDIA via NVML (nvml.dll do driver, sem ring 0 proprio).</summary>
internal sealed class NvmlGpu
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlShutdown();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint clock);

    private const int TempGpu = 0;
    private const int ClockGraphics = 0;

    private IntPtr _device;

    public string Name { get; private set; } = "GPU";

    private NvmlGpu(IntPtr device, string name)
    {
        _device = device;
        Name = name;
    }

    public static NvmlGpu? TryCreate()
    {
        if (nvmlInit_v2() != 0) return null;

        if (nvmlDeviceGetHandleByIndex_v2(0, out var dev) != 0) return null;

        var sb = new StringBuilder(96);
        string name = "GPU";
        if (nvmlDeviceGetName(dev, sb, (uint)sb.Capacity) == 0 && sb.Length > 0) name = sb.ToString();

        return new NvmlGpu(dev, name);
    }

    public GpuRead Read()
    {
        var g = new GpuRead { Name = Name, Source = "nvml" };

        if (nvmlDeviceGetUtilizationRates(_device, out var util) == 0) g.Load = util.Gpu;

        if (nvmlDeviceGetTemperature(_device, TempGpu, out var t) == 0) g.Temp = t;

        if (nvmlDeviceGetMemoryInfo(_device, out var mem) == 0 && mem.Total > 0)
        {
            g.MemUsedMb = (float)Math.Round(mem.Used / 1048576.0, 0);
            g.MemTotalMb = (float)Math.Round(mem.Total / 1048576.0, 0);
        }

        if (nvmlDeviceGetPowerUsage(_device, out var mw) == 0) g.Power = (float)Math.Round(mw / 1000.0, 1);

        if (nvmlDeviceGetEnforcedPowerLimit(_device, out var limit) == 0) g.PowerLimit = (float)Math.Round(limit / 1000.0, 0);

        if (nvmlDeviceGetFanSpeed(_device, out var fan) == 0) g.FanPercent = fan;

        if (nvmlDeviceGetClockInfo(_device, ClockGraphics, out var clock) == 0) g.ClockMhz = clock;

        return g;
    }

    ~NvmlGpu() => _device = IntPtr.Zero;
}

public sealed class GpuRead
{
    public string Name { get; set; } = "GPU";
    public string Source { get; set; } = "lhm";
    public float Load { get; set; }
    public float Temp { get; set; }
    public float MemUsedMb { get; set; }
    public float MemTotalMb { get; set; }
    public float Power { get; set; }
    public float PowerLimit { get; set; }
    public float FanPercent { get; set; }
    public float ClockMhz { get; set; }
}
