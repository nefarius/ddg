# Embedded in the executable. The caller passes data through environment
# variables. No command text is constructed from a path or a drive letter.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    Import-Module "$PSHOME\Modules\Storage\Storage.psd1" -ErrorAction Stop
    $target = $env:DDG_TARGET
    if ($target -match '^[a-zA-Z]:$') {
        $source = Get-Partition -DriveLetter $target[0]
        $image = Get-DiskImage -DevicePath ('\\.\PhysicalDrive' + $source.DiskNumber)
    } else {
        $image = Get-DiskImage -ImagePath $target
    }
    if (!$image.Attached -or $image.ImagePath -notmatch '^[a-zA-Z]:\\.+\.vhdx$') {
        throw 'Select a mounted Dev Drive backed by a local .vhdx file.'
    }
    if ($image.DevicePath -notmatch '^\\\\\.\\PhysicalDrive(\d+)$') { throw 'Cannot identify the attached disk.' }
    $disk = Get-Disk -Number ([uint32]$Matches[1])
    if ($env:DDG_ACTION -eq 'refresh') {
        $disk | Update-Disk
        $disk = Get-Disk -Number $disk.Number
    }
    if ($disk.PartitionStyle -ne 'GPT' -or $disk.IsBoot -or $disk.IsSystem -or
        $disk.IsReadOnly -or $disk.IsOffline -or $disk.HealthStatus -ne 'Healthy') {
        throw 'The VHDX must be a healthy, online, writable GPT data disk.'
    }
    $parts = @(Get-Partition -DiskNumber $disk.Number | Sort-Object Offset)
    $basic = [guid]'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7'
    $msr = [guid]'e3c9e316-0b5c-4db8-817d-f92df00215ae'
    $part = $parts[-1]
    if (!$part -or [guid]$part.GptType -ne $basic -or
        @($parts | Where-Object { [guid]$_.GptType -ne $msr }).Count -ne 1 -or
        $part.IsReadOnly -or $part.IsOffline -or $part.IsBoot -or $part.IsSystem -or $part.IsHidden) {
        throw 'Expected a single writable basic-data partition at the end of the VHDX, optionally preceded by a Microsoft reserved partition.'
    }
    if ($target -match '^[a-zA-Z]:$' -and $part.DriveLetter -ne $target[0]) { throw 'Drive letter changed.' }
    $volume = Get-Volume -Partition $part
    if ($volume.FileSystem -ne 'ReFS' -or $volume.HealthStatus -ne 'Healthy' -or !$volume.Size) {
        throw 'The selected partition must contain a healthy mounted ReFS volume.'
    }
    $diskId = ([guid]$disk.Guid).ToString()
    $partId = ([guid]$part.Guid).ToString()
    if ($diskId -eq [guid]::Empty.ToString() -or $partId -eq [guid]::Empty.ToString()) { throw 'Missing GPT identity.' }
    $max = [uint64]($part | Get-PartitionSupportedSize).SizeMax
    if (!$max) { throw 'Windows did not report a supported partition size.' }
    if ($env:DDG_ACTION -eq 'extend') {
        # Re-resolve the original image and compare stable IDs inside the
        # process performing the write. Pass the checked CIM object to Resize.
        if ($diskId -ne $env:DDG_DISK -or $partId -ne $env:DDG_PART -or
            $volume.Path -ne $env:DDG_VOLUME -or $part.Offset -ne [uint64]$env:DDG_OFFSET -or
            $disk.Size -ne [uint64]$env:DDG_SIZE -or $part.Size -ne [uint64]$env:DDG_OLDPART -or
            $max -ne [uint64]$env:DDG_MAX -or $max -le $part.Size) {
            throw 'Disk identity, layout or supported size changed before partition growth. Run the preview again.'
        }
        Resize-Partition -InputObject $part -Size $max -Confirm:$false
    } else {
        [pscustomobject]@{
            Image = $image.ImagePath; Device = $image.DevicePath; DiskId = $diskId
            PartId = $partId; Volume = $volume.Path; Letter = [string]$part.DriveLetter
            Offset = $part.Offset; DiskSize = $disk.Size; PartSize = $part.Size
            FsSize = $volume.Size; Max = $max; Cluster = $volume.AllocationUnitSize
            HostFree = (Get-Volume -FilePath $image.ImagePath).SizeRemaining
        } | ConvertTo-Json -Compress
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
