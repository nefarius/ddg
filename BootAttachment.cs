using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static class BootAttachment
{
    const string RegistryPath = @"SYSTEM\CurrentControlSet\Control\AutoAttachVirtualDisks";

    public static string? FindRecord(string image)
    {
        // Windows owns this record. Match the stored image path, and let
        // the Virtual Disk API perform all changes.
        using var root = Registry.LocalMachine.OpenSubKey(RegistryPath);
        if (root == null) return null;
        string? match = null;
        foreach (string name in root.GetSubKeyNames())
        {
            using var record = root.OpenSubKey(name);
            if (!StringComparer.OrdinalIgnoreCase.Equals(ReadPath(record), image)) continue;
            if (match != null) throw new IOException($"Windows has multiple boot records for {image}.");
            match = name;
        }
        return match;
    }

    static string? ReadPath(RegistryKey? record) =>
        record?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    public static void Register(SafeFileHandle disk, Guid id, string image, bool apply)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            throw new IOException("Boot registration requires Windows 11 24H2 or later.");
        _ = NativeDisk.IsFixed(disk);
        using var record = Registry.LocalMachine.OpenSubKey($@"{RegistryPath}\{id:B}");
        var registeredPath = ReadPath(record);
        if (registeredPath != null && !StringComparer.OrdinalIgnoreCase.Equals(registeredPath, image))
            throw new IOException($"This VHDX identifier is registered for a different path: {registeredPath}");
        bool matches = FindRecord(image) != null;
        bool loaded = NativeDisk.IsLoaded(disk);
        Console.WriteLine($"Image       {image}");
        Console.WriteLine($"Boot attach {(matches ? "registered" : "no matching Windows registration")}");
        if (loaded)
        {
            if (matches)
            {
                Console.WriteLine($"Already attached as {NativeDisk.PhysicalPath(disk)} and registered for boot.");
                return;
            }
            throw new IOException("The image is in use. Close files and dismount this VHDX before applying boot registration.");
        }
        if (!apply)
        {
            Console.WriteLine("Preview only. --apply attaches the image now and registers it for boot.");
            return;
        }
        NativeDisk.AttachAtBoot(disk);
        string device = NativeDisk.PhysicalPath(disk);
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (FindRecord(image) != null)
            {
                Console.WriteLine($"Attached as {device}. Verified Windows boot registration.");
                return;
            }
            Thread.Sleep(100);
        }
        throw new IOException($"Attached as {device}, but Windows did not record boot attachment for this path.");
    }
}
