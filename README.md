# ORF Log File Watcher with PostgreSQL Ingestion

A .NET 8 application that monitors ORF log files and ingests them into PostgreSQL. This is a dual-purpose application with both a background service and WPF UI component.

## Features

- Monitors ORF log files with real-time processing
- Handles file rotation at midnight
- Parses log entries into structured data
- Stores entries in PostgreSQL database
- Maintains file reading positions to avoid duplicate processing
- Supports multiple deployment modes (Windows Service, Console, WPF)
- User-friendly WPF UI for configuration and monitoring

## Development Status

- ✅ Core file watching service implemented
- ✅ Log file parsing logic implemented
- ✅ File position tracking implemented
- ✅ WPF UI implemented
- 🔄 PostgreSQL integration (in progress)
- 🔄 Windows Service installation (pending)

## Getting Started

### Prerequisites

- .NET 8.0 SDK
- PostgreSQL 15+
- Windows 10/11 or Windows Server

### Building the Project

```bash
dotnet build
```

### Running in Development Mode

```bash
dotnet run --project src/Log2Postgres
```

## License

This project is proprietary and confidential.

## Acknowledgments

- Vamsoft ORF for the log file format specifications 