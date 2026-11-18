using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSerializable(typeof(Layout))]
partial class StorageJson : JsonSerializerContext;

sealed record Layout(string Image, string Device, Guid DiskId, Guid PartId, string Volume,
    string Letter, long Offset, long DiskSize, long PartSize, long FsSize, long Max,
    long Cluster, long HostFree)
{
    public void CheckIdentity(Layout other)
    {
        if (other.DiskId != DiskId || other.PartId != PartId || other.Offset != Offset ||
            !StringComparer.OrdinalIgnoreCase.Equals(other.Volume, Volume) ||
            !StringComparer.OrdinalIgnoreCase.Equals(other.Image, Image))
            throw new IOException("Windows returned a different disk, partition or volume. Stopped.");
    }
}

static class WindowsStorage
{
    static readonly string Command = ReadCommand();

    static string ReadCommand()
    {
        using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("Storage.ps1")!);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()));
    }

    public static Layout Read(string target, bool refresh = false) =>
        JsonSerializer.Deserialize(Run(target, refresh ? "refresh" : "read"), StorageJson.Default.Layout)
        ?? throw new IOException("Windows returned no storage information.");

    public static void Extend(Layout expected) => Run(expected.Image, "extend", expected);

    static string Run(string target, string action, Layout? expected = null)
    {
        string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0");
        var start = new ProcessStartInfo(Path.Combine(ps, "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Command })
            start.ArgumentList.Add(arg);
        start.Environment["PSModulePath"] = Path.Combine(ps, "Modules");
        start.Environment["DDG_TARGET"] = target;
        start.Environment["DDG_ACTION"] = action;
        if (expected is { } s)
        {
            start.Environment["DDG_DISK"] = s.DiskId.ToString();
            start.Environment["DDG_PART"] = s.PartId.ToString();
            start.Environment["DDG_VOLUME"] = s.Volume;
            start.Environment["DDG_OFFSET"] = s.Offset.ToString(CultureInfo.InvariantCulture);
            start.Environment["DDG_SIZE"] = s.DiskSize.ToString(CultureInfo.InvariantCulture);
            start.Environment["DDG_OLDPART"] = s.PartSize.ToString(CultureInfo.InvariantCulture);
            start.Environment["DDG_MAX"] = s.Max.ToString(CultureInfo.InvariantCulture);
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException(stderr.Result.Trim());
        return stdout.Result.Trim();
    }
}
