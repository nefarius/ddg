# <img src="assets/NSS-Vibe.png" height="140" align="left" alt="Nefarius Software Solutions logo" /> Dev Drive Grow (DDG)

[![.NET build](https://github.com/nefarius/ddg/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/nefarius/ddg/actions/workflows/build.yml)
[![Windows 11 x64](https://img.shields.io/badge/Windows%2011-x64-0078D4)](https://learn.microsoft.com/windows/dev-drive/)
[![Built with .NET 10](https://img.shields.io/badge/Built%20with-.NET%2010-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![Assisted by Codex](https://img.shields.io/badge/Assisted%20by-Codex-8B5CF6)](https://openai.com/codex/)

Grow a mounted Windows Dev Drive's VHDX, GPT partition, and ReFS filesystem in one command.

<br clear="all" />

> [!NOTE]
> This project was built with assistance from an AI coding agent. See the disposable-drive test below for the behavior verified on Windows.

## About

DDG uses the Windows Virtual Disk API for image growth and Windows Storage for partition growth. It reads the final filesystem capacity directly from ReFS.

## Features

- Preview the backing image, volume identity, current capacities, host free space, and Windows boot registration.
- Grow an attached VHDX and its ReFS partition in place. Retrying with the same absolute target resumes partial growth.
- Verify that an existing Windows boot-attachment record survives resize.
- Mount a detached VHDX and register it for startup through Windows on 24H2 or later.

## Supported systems

- **Runtime** Windows 11 with Dev Drive enabled. The self-contained distribution targets x64 and bundles .NET 10.
- **Image layout** Local, standalone fixed or dynamically expanding VHDX with a writable, healthy GPT disk. The last partition must be the sole basic-data partition and contain a healthy ReFS Dev Drive. Microsoft reserved partitions can precede it.
- **Permissions** Run in an administrator shell. You can also prefix the examples with gsudo.
- **Boot registration** Windows 11 24H2 or later, with the backing volume accessible at startup.

The tool grows images only. Keep a current backup and use a standalone image outside any checkpoint chain. Dynamic VHDXs can consume additional physical host space over time.

## Quick start

Build the executable using the commands below, then run these examples from the repository root in an administrator shell. Replace D: and 256GiB with your drive and target.

~~~powershell
.\publish\devdrive-grow.exe D: --to 256GiB
.\publish\devdrive-grow.exe D: --to 256GiB --apply
~~~

The first command previews the operation. The second grows the image and extends the partition and ReFS into available contiguous space. A quoted VHDX path works in place of the drive letter. The target is the **final virtual disk capacity** and accepts whole MiB, GiB, or TiB. Partition metadata and ReFS allocation granularity reduce usable capacity.

If growth stops halfway, inspect the preview and rerun with the same target once the storage error is resolved. The tool reads the actual sizes and continues from the completed layers.

### Restore boot attachment

Use the full path to the detached VHDX. If it is currently mounted without registration, close files on it and dismount it first.

~~~powershell
.\publish\devdrive-grow.exe "C:\Dev Drives\work.vhdx" --boot-attach
.\publish\devdrive-grow.exe "C:\Dev Drives\work.vhdx" --boot-attach --apply
~~~

The apply command mounts the image and reads back its Windows startup record. An already mounted image with a matching registration makes this command a no-op.

## Build and test

Use the .NET 10 SDK on Windows.

~~~powershell
dotnet build DevDriveGrow.csproj -c Release
dotnet publish DevDriveGrow.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o publish
.\publish\devdrive-grow.exe --help
~~~

CI builds the CLI on Windows and runs the help command. To test disk changes, run the following from Windows PowerShell 5.1 as administrator on Windows 11 24H2 or later. Choose an ASCII scratch directory on a fixed local disk.

~~~powershell
.\Test-DisposableDrive.ps1 -Executable .\publish\devdrive-grow.exe -Scratch C:\Temp
~~~

The test creates and removes a disposable Dev Drive. It checks boot registration, live growth with a file open, the final capacity and file hash, and same-target retries. Boot verification reads Windows' registration record. The test leaves a transcript and performs no reboot.

## Support

Report reproducible DDG defects with the Windows build, exact command, and relevant output. Include a disposable-drive transcript if it reproduces the issue. Remove sensitive paths and volume labels before posting. [GitHub Issues](https://github.com/nefarius/ddg/issues)

## License

MIT. [License text](LICENSE)

## Sources

- [Microsoft Dev Drive documentation](https://learn.microsoft.com/windows/dev-drive/)
- [Windows Virtual Disk attach flags](https://learn.microsoft.com/windows/win32/api/virtdisk/ne-virtdisk-attach_virtual_disk_flag)
- [Windows Storage Resize-Partition](https://learn.microsoft.com/powershell/module/storage/resize-partition)
- [ReFS volume extension control](https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_extend_volume)
