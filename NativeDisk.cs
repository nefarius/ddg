using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

static class NativeDisk
{
    [StructLayout(LayoutKind.Sequential)]
    struct StorageType { public uint Device; public Guid Vendor; }
    [StructLayout(LayoutKind.Sequential)]
    struct OpenParameters { public int Version, InfoOnly, ReadOnly; public Guid Resiliency; }
    [StructLayout(LayoutKind.Sequential)]
    struct Growth { public int Version; public long Size; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct AttachParameters { [FieldOffset(0)] public int Version; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct DiskInfo
    {
        [FieldOffset(0)] public int Version;
        [FieldOffset(8)] public long Size;
        [FieldOffset(8)] public uint Subtype;
        [FieldOffset(8)] public Guid Id;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct VolumeState { public uint Flags, Mask, Version, Reserved; }
    [StructLayout(LayoutKind.Explicit, Size = 512)]
    struct RefsData
    {
        [FieldOffset(24)] public long Sectors;
        [FieldOffset(56)] public uint SectorSize;
        [FieldOffset(60)] public uint ClusterSize;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int OpenVirtualDisk(ref StorageType type, string path, int access, int flags,
        ref OpenParameters options, out SafeFileHandle handle);
    [DllImport("virtdisk.dll")]
    static extern int GetVirtualDiskInformation(SafeFileHandle handle, ref int bytes,
        ref DiskInfo info, IntPtr used);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref int bytes, StringBuilder path);
    [DllImport("virtdisk.dll")]
    static extern int ExpandVirtualDisk(SafeFileHandle handle, int flags, ref Growth growth, IntPtr overlapped);
    [DllImport("virtdisk.dll")]
    static extern int AttachVirtualDisk(SafeFileHandle handle, IntPtr security, uint flags,
        uint providerFlags, ref AttachParameters parameters, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref VolumeState input, int inputBytes,
        out VolumeState output, int outputBytes, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputBytes,
        out RefsData output, int outputBytes, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref long input, int inputBytes,
        IntPtr output, int outputBytes, out int returned, IntPtr overlapped);

    static void Check(int error, string operation)
    {
        if (error != 0)
            throw new IOException($"{operation} failed ({error:X8}): {new Win32Exception(error).Message}");
    }

    public static SafeFileHandle Open(string image, bool write)
    {
        var type = new StorageType { Device = 3, Vendor = new Guid("ec984aec-a0f9-47e9-901f-71415a66345b") };
        var options = new OpenParameters { Version = 2, InfoOnly = write ? 0 : 1 };
        Check(OpenVirtualDisk(ref type, image, 0, 0, ref options, out var handle), "Open VHDX");
        return handle;
    }

    static DiskInfo Info(SafeFileHandle handle, int version)
    {
        var info = new DiskInfo { Version = version };
        int bytes = Marshal.SizeOf<DiskInfo>();
        Check(GetVirtualDiskInformation(handle, ref bytes, ref info, IntPtr.Zero), "Read VHDX metadata");
        return info;
    }

    public static long Size(SafeFileHandle handle) => Info(handle, 1).Size;
    public static Guid Id(SafeFileHandle handle) => Info(handle, 2).Id;
    public static bool IsLoaded(SafeFileHandle handle) => Info(handle, 13).Subtype != 0;
    public static bool IsFixed(SafeFileHandle handle) => Info(handle, 7).Subtype switch
    {
        2 => true, 3 => false,
        _ => throw new IOException("Only standalone fixed and dynamically expanding VHDX images are supported.")
    };

    public static void CheckDevice(SafeFileHandle handle, Layout layout)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(PhysicalPath(handle), layout.Device))
            throw new IOException("The VHDX attachment changed during inspection.");
    }

    public static string PhysicalPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(512);
        int bytes = path.Capacity * sizeof(char);
        Check(GetVirtualDiskPhysicalPath(handle, ref bytes, path), "Resolve attached VHDX");
        return path.ToString();
    }

    public static void AttachAtBoot(SafeFileHandle handle)
    {
        const uint permanentLifetime = 0x4;
        const uint atBoot = 0x400;
        var parameters = new AttachParameters { Version = 1 };
        Check(AttachVirtualDisk(handle, IntPtr.Zero, permanentLifetime | atBoot, 0, ref parameters, IntPtr.Zero),
            "Attach VHDX and register it for boot");
    }

    public static SafeFileHandle OpenDevVolume(string volume, bool write)
    {
        if (!volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(volume[10..].TrimEnd('\\'), "B", out _))
            throw new IOException("Expected a Windows volume GUID path.");
        // Query the persistent Dev Drive flag on the filesystem root. The raw
        // device handle below is needed separately for ReFS geometry and growth.
        using var root = CreateFile(volume, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (root.IsInvalid) Check(Marshal.GetLastWin32Error(), "Open volume root");
        RequireDevVolume(root);
        var handle = CreateFile(volume.TrimEnd('\\'), write ? 0xc0000000u : 0x80000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) Check(Marshal.GetLastWin32Error(), "Open volume");
        return handle;
    }

    static void RequireDevVolume(SafeFileHandle handle)
    {
        const uint developerVolume = 0x2000;
        var input = new VolumeState { Version = 1, Mask = developerVolume };
        int bytes = Marshal.SizeOf<VolumeState>();
        if (!DeviceIoControl(handle, 0x9023c, ref input, bytes, out var output, bytes, out int returned, IntPtr.Zero))
            Check(Marshal.GetLastWin32Error(), "Query Dev Drive designation");
        if (returned < bytes || (output.Flags & developerVolume) == 0)
            throw new IOException("Windows does not designate this ReFS volume as a Dev Drive.");
    }

    public static (long Bytes, uint Sector, uint Cluster) Geometry(SafeFileHandle handle)
    {
        if (!DeviceIoControl(handle, 0x902d8, IntPtr.Zero, 0, out RefsData data, Marshal.SizeOf<RefsData>(),
            out int returned, IntPtr.Zero)) Check(Marshal.GetLastWin32Error(), "Read ReFS geometry");
        if (returned < 64 || data.Sectors <= 0 || data.SectorSize == 0 || data.ClusterSize < data.SectorSize)
            throw new IOException("ReFS returned incomplete volume geometry.");
        return (checked(data.Sectors * data.SectorSize), data.SectorSize, data.ClusterSize);
    }

    public static void ExtendFileSystem(SafeFileHandle handle, long partitionBytes)
    {
        var geometry = Geometry(handle);
        if (partitionBytes <= geometry.Bytes || partitionBytes % geometry.Sector != 0)
            throw new IOException("Invalid ReFS extension boundary.");
        long sectors = partitionBytes / geometry.Sector;
        if (!DeviceIoControl(handle, 0x900f0, ref sectors, sizeof(long), IntPtr.Zero, 0, out _, IntPtr.Zero))
            Check(Marshal.GetLastWin32Error(), "Extend ReFS to the partition boundary");
    }

    public static void Expand(SafeFileHandle handle, long target)
    {
        var growth = new Growth { Version = 1, Size = target };
        Check(ExpandVirtualDisk(handle, 0, ref growth, IntPtr.Zero), "Expand VHDX");
    }
}
