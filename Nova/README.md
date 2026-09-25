# Nova

Nova is a standalone Windows optimization dashboard designed to keep your PC clean, monitored, and running efficiently.

## Features

- CPU, memory, disk, and network monitoring
- Galaxy-themed dashboard interface
- Startup application overview
- Temporary file cleanup
- Recycle Bin cleanup
- Large file review and cleanup recommendations
- Safe quarantine workflow for unexpected removals
- Cleanup and action logging

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK

## Run locally

```bash
dotnet restore

dotnet build "Nova/Nova.csproj" -c Release

dotnet run --project "Nova/Nova.csproj"
```

## Create a single-file Windows app

```bash
dotnet publish "Nova/Nova.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:UseAppHost=true
```

The output will be in:

```text
Nova/bin/Release/net8.0-windows/win-x64/publish/
```

## Notes

This version prioritizes safety. Cleanup actions move content into a local quarantine folder before removal so you can review or restore if needed.
