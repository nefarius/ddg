# Agent guidance

## Project

- DDG is a .NET 10 CLI for standalone VHDX-backed Windows Dev Drives. Keep runtime dependencies limited to .NET and built-in Windows storage components.
- Keep command parsing and orchestration in Program.cs, Windows interop in NativeDisk.cs, boot registration in BootAttachment.cs, and CIM storage operations in WindowsStorage.cs and Storage.ps1.
- Save text files as UTF-8 without a BOM. Keep generated binaries, VHDXs, fixture logs, and scratch files out of Git.
- Use master as the base branch and Conventional Commits for commit subjects.

## Storage behavior

- Keep preview read-only and require --apply for changes to images, partitions, ReFS, or boot registration.
- Keep resize grow-only and online. Verify the attached image, disk, partition, and volume identities before storage writes. Preserve the ability to resume with the same absolute target after partial growth.
- Use the Virtual Disk API to register boot attachment. Read the Windows registration for verification and leave writes to Windows.
- Pass path arguments to PowerShell as data. Keep elevated tests confined to newly created disposable images. Report boot-record verification and reboot verification separately.

## Verification

- Run dotnet build .\DevDriveGrow.csproj -c Release and check --help for CLI changes.
- Run Test-DisposableDrive.ps1 with a published executable on Windows 11 24H2 or later for disk or boot behavior changes. The test requires administrator access and creates and removes its own VHDX.
- The GitHub Actions workflow covers build and non-elevated CLI smoke checks. Keep elevated disk tests local.

## Documentation

- Lead with the purpose and supported environment. Keep preview, apply, recovery, and build commands reproducible.
- Keep badges tied to actual project requirements or configured services. Update README.md and --help together when CLI behavior changes.
