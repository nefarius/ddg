using System.Globalization;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    const long MiB = 1L << 20;
    const long GiB = 1L << 30;
    const long TiB = 1L << 40;
    const string Usage = """
        devdrive-grow <drive: | image.vhdx> --to <size> [--apply]
        devdrive-grow <image.vhdx> --boot-attach [--apply]

        Preview growth of a mounted Windows Dev Drive. Add --apply to commit.
        --to sets the final VHDX capacity (whole MiB, GiB or TiB, up to 64 TiB).
        The data partition and ReFS take all available contiguous space.
        --boot-attach attaches a detached VHDX now and registers it for startup.

        devdrive-grow D: --to 256GiB
        devdrive-grow "C:\Dev Drives\work.vhdx" --to 256GiB --apply
        Run from an elevated terminal. Repeating the same target resumes growth.
        """;

    static int Main(string[] args)
    {
        try
        {
            if (args is ["--help"] or ["-h"] or ["/?"]) { Console.WriteLine(Usage); return 0; }
            bool bootAttach = args is [_, "--boot-attach"] or [_, "--boot-attach", "--apply"];
            if (!bootAttach && args is not [_, "--to", _] and not [_, "--to", _, "--apply"])
                throw new ArgumentException(Usage);
            bool apply = args[^1] == "--apply";
            long target = bootAttach ? 0 : ParseSize(args[2]);
            string input = args[0];
            if (bootAttach || !Regex.IsMatch(input, "^[A-Za-z]:$"))
            {
                if (!Path.GetExtension(input).Equals(".vhdx", StringComparison.OrdinalIgnoreCase) || !File.Exists(input))
                    throw new ArgumentException(bootAttach ? "Specify an existing .vhdx file for boot attachment."
                        : "Specify a mounted drive letter such as D: or an existing .vhdx file.");
                input = Path.GetFullPath(input);
            }
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                throw new IOException("Windows 11 with Dev Drive support is required.");
            using var user = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
                throw new IOException("Open Windows Terminal as administrator, then run this command again.");
            if (bootAttach)
            {
                if (!Regex.IsMatch(input, @"^[A-Za-z]:\\") ||
                    new DriveInfo(Path.GetPathRoot(input)!).DriveType != DriveType.Fixed)
                    throw new ArgumentException("Boot attachment requires a VHDX on a fixed local drive.");
                using var disk = NativeDisk.Open(input, apply);
                WithDiskLock(disk, id => BootAttachment.Register(disk, id, input, apply));
            }
            else Grow(input, target, apply);
            return 0;
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    static long ParseSize(string text)
    {
        var match = Regex.Match(text, @"^([1-9][0-9]*)(MiB|GiB|TiB)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
            throw new ArgumentException("Use a positive whole binary size such as 256GiB.");
        long unit = match.Groups[2].Value.ToUpperInvariant() switch { "MIB" => MiB, "GIB" => GiB, _ => TiB };
        if (count > 64 * TiB / unit) throw new ArgumentException("VHDX capacity exceeds 64 TiB.");
        return count * unit;
    }

    static string Capacity(long bytes) => $"{(decimal)bytes / GiB:0.###} GiB ({bytes:N0} bytes)";

    static void WithDiskLock(SafeFileHandle disk, Action<Guid> action)
    {
        var id = NativeDisk.Id(disk);
        if (id == Guid.Empty) throw new IOException("VHDX has no usable identifier.");
        using var gate = new Mutex(false, $@"Global\DevDriveGrow-{id:N}");
        bool held;
        try { held = gate.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("Another operation is running for this VHDX.");
        try { action(id); }
        finally { gate.ReleaseMutex(); }
    }

    static void Grow(string input, long target, bool apply)
    {
        var initial = WindowsStorage.Read(input);
        using var disk = NativeDisk.Open(initial.Image, apply);
        WithDiskLock(disk, _ =>
        {
            var layout = WindowsStorage.Read(input);
            initial.CheckIdentity(layout);
            NativeDisk.CheckDevice(disk, layout);
            bool fixedImage = NativeDisk.IsFixed(disk);
            long old = NativeDisk.Size(disk);
            if (target < old) throw new IOException($"Target is smaller than the current VHDX capacity of {Capacity(old)}. Growth only.");
            if (layout.DiskSize > old || layout.PartSize <= 0 || layout.Offset <= 0 || layout.PartSize > old - layout.Offset)
                throw new IOException("VHDX and partition geometry disagree.");
            using var volume = NativeDisk.OpenDevVolume(layout.Volume, apply);
            var geometry = NativeDisk.Geometry(volume);
            if (geometry.Bytes > layout.PartSize) throw new IOException("ReFS exceeds the reported partition boundary.");
            Console.WriteLine($"Image       {layout.Image} ({(fixedImage ? "fixed" : "dynamic")})");
            string letter = layout.Letter.TrimEnd('\0');
            Console.WriteLine($"Dev Drive   {(letter.Length == 0 ? "" : letter + ":  ")}{layout.Volume}");
            Console.WriteLine($"VHDX        {Capacity(old)} -> {Capacity(target)}");
            Console.WriteLine($"Partition   {Capacity(layout.PartSize)}");
            Console.WriteLine($"ReFS        {Capacity(geometry.Bytes)}");
            Console.WriteLine($"Host free   {Capacity(layout.HostFree)}");
            string? bootRecord = BootAttachment.FindRecord(layout.Image);
            Console.WriteLine($"Boot attach {(bootRecord != null ? "registered" : "no matching Windows registration")}");
            if (fixedImage && target - old > layout.HostFree) throw new IOException("The host lacks free space for this fixed VHDX expansion.");
            if (!fixedImage && target > layout.HostFree)
                Console.WriteLine("Dynamic allocation: future writes depend on available host space.");
            if (!apply)
            {
                Console.WriteLine("Preview only. --apply grows the image if needed, then fills the last partition and ReFS to Windows' supported limit.");
                return;
            }
            if (target > old)
            {
                Console.WriteLine("Growing the attached VHDX...");
                NativeDisk.Expand(disk, target);
            }
            if (NativeDisk.Size(disk) != target)
                throw new IOException("VHDX size verification failed. Run the preview before retrying.");
            Console.WriteLine("Checking the attached disk and its supported partition size...");
            var current = ReadAtCapacity(layout, disk, target);
            if (current.PartSize < current.Max)
            {
                Console.WriteLine($"Extending partition and ReFS to {Capacity(current.Max)}...");
                WindowsStorage.Extend(current);
            }
            var end = WindowsStorage.Read(layout.Image);
            layout.CheckIdentity(end);
            NativeDisk.CheckDevice(disk, end);
            if (end.DiskSize != target || end.PartSize != Math.Max(current.Max, current.PartSize))
                throw new IOException("Partition size verification failed. Run the same target again to inspect and resume.");
            // ReFS exposes complete allocation containers. For a default
            // Dev Drive the final partial 64 MiB container is unusable.
            const long refsContainer = 64 * MiB;
            long minimumFs = end.PartSize - end.PartSize % refsContainer;
            var fs = NativeDisk.Geometry(volume);
            if (fs.Bytes < minimumFs)
            {
                Console.WriteLine("Completing ReFS growth inside the verified partition...");
                NativeDisk.ExtendFileSystem(volume, end.PartSize);
                fs = NativeDisk.Geometry(volume);
            }
            if (fs.Bytes > end.PartSize || fs.Bytes < minimumFs || fs.Bytes < geometry.Bytes)
                throw new IOException($"ReFS reports {Capacity(fs.Bytes)} inside a {Capacity(end.PartSize)} partition. Expected at least {Capacity(minimumFs)}.");
            Console.WriteLine($"Verified    VHDX {Capacity(target)} | partition {Capacity(end.PartSize)} | ReFS {Capacity(fs.Bytes)}");
            if (bootRecord != null)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(bootRecord, BootAttachment.FindRecord(end.Image)))
                    throw new IOException("Growth completed, but Windows boot registration changed. Dismount the VHDX and run --boot-attach --apply with its full path.");
                Console.WriteLine("Verified    Windows boot registration preserved.");
            }
        });
    }

    static Layout ReadAtCapacity(Layout initial, SafeFileHandle disk, long target)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var now = WindowsStorage.Read(initial.Image, refresh: true);
            initial.CheckIdentity(now);
            NativeDisk.CheckDevice(disk, now);
            if (now.DiskSize == target && now.Max > 0 && now.Max <= target - now.Offset &&
                target - now.Offset - Math.Max(now.PartSize, now.Max) <= 2 * MiB)
                return now;
            if (attempt < 3) Thread.Sleep(750);
        }
        throw new IOException("Windows has not exposed the expected contiguous space. Run the same target again after refreshing the disk.");
    }
}
