using System.Runtime.InteropServices;

namespace PinSentinel.Core;

public interface IPinSensor
{
    /// <summary>Returns null when the read failed or the frame was not plausible.</summary>
    PinFrame? Read();
}

public sealed record GpuInfo(int Index, uint DeviceId, uint SubSystemId)
{
    public string SubSystemText => $"{SubSystemId & 0xFFFF:X4}:{SubSystemId >> 16:X4}";
}

/// <summary>
/// Reads the ITE IT8915FN on ASUS ROG Astral cards through the NVIDIA driver's
/// I2C interface. Read-only: NvAPI_I2CWriteEx is deliberately never resolved,
/// because a write to the wrong device on a GPU I2C bus can damage the card.
/// </summary>
public sealed unsafe class AstralSensor : IPinSensor
{
    // Subsystem IDs as NVAPI reports them (device << 16 | vendor).
    public static readonly IReadOnlyDictionary<uint, string> SupportedCards = new Dictionary<uint, string>
    {
        [0x89E31043] = "ROG Astral RTX 5090 OC",
        [0x89EA1043] = "ROG Astral RTX 5090D OC",
        [0x89EC1043] = "ROG Astral RTX 5090 LC",
        [0x8A2E1043] = "ROG Astral RTX 5090 OC White",
        [0x8A611043] = "ROG Matrix RTX 5090",
        [0x89DE1043] = "ROG Astral RTX 5080 OC",
        [0x89DF1043] = "ROG Astral RTX 5080",
        [0x8A2B1043] = "ROG Astral RTX 5080 OC White",
    };

    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetPciIdentifiers = 0x2DDFB66E;
    private const uint IdI2CReadEx = 0x4D7B0709;

    private const int MaxPhysicalGpus = 64;
    private const byte ChipAddress7Bit = 0x2B;
    private const byte DataRegister = 0x80;
    private const byte I2CPort = 1;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    // NV_I2C_INFO_V3, 64 bytes on x64.
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct I2CInfo
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DeviceAddress;
        public byte* RegAddress;
        public uint RegAddressSize;
        public byte* Data;
        public uint Size;
        public uint SpeedDeprecated;
        public uint SpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    private static bool s_initialized;
    private static delegate* unmanaged[Cdecl]<IntPtr*, uint*, int> s_enumGpus;
    private static delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, uint*, int> s_getPciIds;
    private static delegate* unmanaged[Cdecl]<IntPtr, I2CInfo*, uint*, int> s_i2cRead;

    private readonly IntPtr _gpu;

    public GpuInfo Gpu { get; }
    public int LastStatus { get; private set; }

    private AstralSensor(IntPtr gpu, GpuInfo info)
    {
        _gpu = gpu;
        Gpu = info;
    }

    private static void EnsureInitialized()
    {
        if (s_initialized) return;
        if (sizeof(I2CInfo) != 64) throw new InvalidOperationException("NV_I2C_INFO layout mismatch");

        var init = (delegate* unmanaged[Cdecl]<int>)Resolve(IdInitialize);
        int status = init();
        if (status != 0) throw new InvalidOperationException($"NvAPI_Initialize failed ({status})");

        s_enumGpus = (delegate* unmanaged[Cdecl]<IntPtr*, uint*, int>)Resolve(IdEnumPhysicalGpus);
        s_getPciIds = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, uint*, int>)Resolve(IdGetPciIdentifiers);
        s_i2cRead = (delegate* unmanaged[Cdecl]<IntPtr, I2CInfo*, uint*, int>)Resolve(IdI2CReadEx);
        s_initialized = true;
    }

    private static IntPtr Resolve(uint id)
    {
        IntPtr p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"NVAPI function 0x{id:X8} not available");
        return p;
    }

    private static List<(IntPtr Handle, GpuInfo Info)> Enumerate()
    {
        EnsureInitialized();
        var handles = stackalloc IntPtr[MaxPhysicalGpus];
        uint count = 0;
        int status = s_enumGpus(handles, &count);
        if (status != 0) throw new InvalidOperationException($"NvAPI_EnumPhysicalGPUs failed ({status})");

        var result = new List<(IntPtr, GpuInfo)>();
        for (int i = 0; i < count; i++)
        {
            uint device = 0, subsys = 0, revision = 0, ext = 0;
            if (s_getPciIds(handles[i], &device, &subsys, &revision, &ext) != 0) continue;
            result.Add((handles[i], new GpuInfo(i, device, subsys)));
        }
        return result;
    }

    public static IReadOnlyList<GpuInfo> ListGpus() => Enumerate().Select(g => g.Info).ToList();

    /// <summary>Opens the first supported card, or null if none is installed.</summary>
    public static AstralSensor? Open()
    {
        foreach (var (handle, info) in Enumerate())
            if (SupportedCards.ContainsKey(info.SubSystemId))
                return new AstralSensor(handle, info);
        return null;
    }

    /// <summary>Reads the raw 24-byte block. Returns false and sets LastStatus on failure.</summary>
    public bool TryReadRaw(Span<byte> buffer)
    {
        if (buffer.Length < PinFrame.RawLength) throw new ArgumentException("buffer too small", nameof(buffer));

        byte reg = DataRegister;
        uint unknown = 0;
        fixed (byte* data = buffer)
        {
            var info = new I2CInfo
            {
                Version = (3u << 16) | 64u,
                DeviceAddress = ChipAddress7Bit << 1,
                RegAddress = &reg,
                RegAddressSize = 1,
                Data = data,
                Size = PinFrame.RawLength,
                SpeedDeprecated = 0xFFFF,
                SpeedKhz = 0,
                PortId = I2CPort,
                IsPortIdSet = 1,
            };
            LastStatus = s_i2cRead(_gpu, &info, &unknown);
        }
        return LastStatus == 0;
    }

    public PinFrame? Read()
    {
        Span<byte> raw = stackalloc byte[PinFrame.RawLength];
        if (!TryReadRaw(raw)) return null;
        return PinFrame.TryParse(raw, DateTimeOffset.Now, out var frame) ? frame : null;
    }
}
