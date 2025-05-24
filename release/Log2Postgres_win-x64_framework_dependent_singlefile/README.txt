ORF Log2Postgres - Framework-Dependent Release
==============================================

This is a framework-dependent single-file deployment of ORF Log2Postgres.

REQUIREMENTS:
- .NET 8 Desktop Runtime must be installed on the target machine
- Download from: https://dotnet.microsoft.com/download/dotnet/8.0/runtime

FILES:
- Log2Postgres.exe    (~14 MB) - Main application executable
- appsettings.json    - Configuration file with default settings
- Log2Postgres.pdb    - Debug symbols (optional, for debugging)

USAGE:
1. Install .NET 8 Desktop Runtime if not already installed
2. Edit appsettings.json to configure your database and log settings
3. Run: Log2Postgres.exe

ADVANTAGES:
- Small file size (~14 MB)
- Faster startup time
- Shared runtime benefits from system updates

DISADVANTAGES:
- Requires .NET 8 runtime installation
- Cannot run on machines without .NET 8

NOTE: Self-contained version not included due to GitHub file size limits (~200MB).
Build locally using: dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true 