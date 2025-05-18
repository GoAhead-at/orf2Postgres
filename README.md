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

## Running the Application

### Normal Mode

Run the application without any arguments to use it in normal mode:

```bash
Log2Postgres.exe
```

### Debug Mode

If you encounter issues with the application where no window appears but the process is running in the background:

1. Open Task Manager and kill any running "Log2Postgres" processes
2. Run the application in debug mode by passing the --debug flag:

```bash
Log2Postgres.exe --debug
```

This will start a simplified version of the application with diagnostic capabilities.

## Development Notes

### Debugging Hidden Processes

If the application runs but doesn't show a window:
- Check the diagnostic logs in `bin/Debug/net8.0-windows/` 
- Look for `diagnostics.txt` which contains system information and process details
- Ensure there are no file locks preventing the application from starting

### Error Handling

The application has been improved with better error handling:
- The application will now show a simplified window if it encounters initialization errors
- Detailed logs are generated to track down startup issues
- Improved handling of missing config files and DI container issues

### Log Filter Toggle Behavior

The Log Filter Toggle buttons:
- When toggled, they filter log entries by type (Info, Warning, Error)
- They change color (to green) when toggled on

### Position File Handling

The application will:
- Try to read the position file on startup
- If it doesn't exist, create a new one with initial position (0)
- Handle deletion of the position file during runtime 