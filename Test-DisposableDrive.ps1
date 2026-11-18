#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$Executable,
      [string]$Scratch = $env:TEMP)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = Join-Path $PSHOME 'Modules'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$root = Join-Path (Resolve-Path -LiteralPath $Scratch).Path ('devdrive-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$image = Join-Path $root 'test & build.vhdx'
$scriptFile = Join-Path $root 'create.txt'
$transcript = Join-Path $root 'result.log'
Start-Transcript -LiteralPath $transcript
try {
    # DiskPart is used only to create a brand-new empty growable test file.
    if ($image -match '[^\x20-\x7e]') { throw 'DiskPart fixture needs an ASCII scratch directory.' }
    ('create vdisk file="' + $image + '" maximum=53248 type=expandable'), 'exit' |
        Set-Content -LiteralPath $scriptFile -Encoding ASCII
    & "$env:SystemRoot\System32\diskpart.exe" /s $scriptFile
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $image)) { throw 'Failed to create disposable VHDX.' }
    $mounted = Mount-DiskImage -ImagePath $image -PassThru
    if ($mounted.ImagePath -ne $image -or $mounted.DevicePath -notmatch '^\\\\\.\\PhysicalDrive(\d+)$') {
        throw 'Fixture attachment mismatch.'
    }
    $disk = Get-Disk -Number ([uint32]$Matches[1])
    if ($disk.BusType -ne 'File Backed Virtual' -or $disk.PartitionStyle -ne 'RAW' -or
        $disk.Size -ne 52GB -or $disk.IsBoot -or $disk.IsSystem) { throw 'Fixture is not a new blank virtual disk.' }
    $disk = $disk | Initialize-Disk -PartitionStyle GPT -PassThru
    $part = $disk | New-Partition -Size 50GB -AssignDriveLetter
    $vol = Format-Volume -Partition $part -FileSystem ReFS -DevDrive -NewFileSystemLabel DDGTEST -Confirm:$false
    $drive = [string]$part.DriveLetter + ':'
    $file = Join-Path ($drive + '\') 'preserve.txt'
    $bytes = [Text.Encoding]::UTF8.GetBytes([guid]::NewGuid().ToString())
    $writer = [IO.File]::Open($file, 'CreateNew', 'Write')
    try {
        $writer.Write($bytes, 0, $bytes.Length)
        $writer.Flush($true)
    } finally {
        $writer.Dispose()
    }
    $hash = (Get-FileHash -LiteralPath $file).Hash
    $bootKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\AutoAttachVirtualDisks'
    $null = Dismount-DiskImage -ImagePath $image
    & $exe $image --boot-attach
    if ($LASTEXITCODE -ne 0 -or (Get-DiskImage -ImagePath $image).Attached) { throw 'Boot-attach preview changed the fixture.' }
    & $exe $image --boot-attach --apply
    if ($LASTEXITCODE -ne 0) {
        $observed = @(Get-ChildItem -LiteralPath $bootKey | Where-Object { $_.GetValue('Path') -eq $image })
        throw "Boot attachment failed. Windows reports $($observed.Count) matching boot records."
    }
    $bootRecord = @(Get-ChildItem -LiteralPath $bootKey | Where-Object { $_.GetValue('Path') -eq $image })
    if ($bootRecord.Count -ne 1) { throw "Expected one boot record. Found $($bootRecord.Count)." }
    if ((Get-FileHash -LiteralPath $file).Hash -ne $hash) {
        throw "The file hash changed after boot attachment at $drive."
    }
    $bootRecordPath = $bootRecord[0].PSPath
    $oldFs = $vol.Size
    & $exe $drive --to 54GiB
    if ($LASTEXITCODE -ne 0 -or (Get-DiskImage -ImagePath $image).Size -ne 52GB -or
        (Get-Volume -DriveLetter $part.DriveLetter).Size -ne $oldFs) { throw 'Preview failed or mutated the fixture.' }
    # Reproduce a prior run stopping after image growth. Hold a shared file
    # open across both operations, as a compiler or editor might.
    $pinned = [IO.File]::Open($file, 'Open', 'Read', 'ReadWrite')
    $bootResult = & $exe $image --boot-attach --apply
    if ($LASTEXITCODE -ne 0 -or ($bootResult -join ' ') -notmatch 'Already attached') { throw 'Same-image boot registration retry failed.' }
    & $exe $image --to 52GiB --apply
    if ($LASTEXITCODE -ne 0 -or (Get-Volume -DriveLetter $part.DriveLetter).Size -le $oldFs) {
        throw 'Recovery of existing unallocated space failed.'
    }
    $part = Get-Partition -DriveLetter $part.DriveLetter
    $oldFs = (Get-Volume -Partition $part).Size
    & $exe $image --to 54GiB --apply
    if ($LASTEXITCODE -ne 0) { throw 'First growth failed.' }
    $newPart = Get-Partition -DriveLetter $part.DriveLetter
    $newFs = (Get-Volume -Partition $newPart).Size
    if ((Get-DiskImage -ImagePath $image).Size -ne 54GB -or $newPart.Guid -ne $part.Guid -or
        $newPart.Size -lt $part.Size + 2GB - 1MB -or $newFs -lt $oldFs + 2GB - 2MB -or
        (Get-FileHash -LiteralPath $file).Hash -ne $hash) { throw 'Capacity, identity or data check failed.' }
    & $exe $drive --to 54GiB --apply
    if ($LASTEXITCODE -ne 0 -or (Get-Volume -DriveLetter $part.DriveLetter).Size -ne $newFs) { throw 'Same-target retry failed.' }
    & $exe $image --to 53GiB --apply
    if ($LASTEXITCODE -eq 0 -or (Get-DiskImage -ImagePath $image).Size -ne 54GB) { throw 'Shrink guard failed.' }
    if ((Get-Item -LiteralPath $bootRecordPath).GetValue('Path') -ne $image) { throw 'Boot registration changed during growth.' }
    Write-Output 'PASS: preview, boot attachment and registration preservation, partial-growth recovery, online growth with an open file, partition and filesystem growth, drive identity, data hash, same-target retry and shrink refusal.'
} catch {
    Write-Output ('FAIL: ' + $_.Exception.Message)
    throw
} finally {
    if ($pinned) { $pinned.Dispose() }
    if (Test-Path -LiteralPath $image) {
        if ((Get-DiskImage -ImagePath $image).Attached) { Dismount-DiskImage -ImagePath $image }
        Remove-Item -LiteralPath $image
    }
    Stop-Transcript
    if ($bootRecordPath -and (Test-Path -LiteralPath $bootRecordPath)) { throw 'Windows retained the disposable VHDX boot record after dismount.' }
}
