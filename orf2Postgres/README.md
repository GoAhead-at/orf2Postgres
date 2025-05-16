# orf2Postgres

A Windows Service and WPF UI application for monitoring ORF log files and writing them to a PostgreSQL database.

## Features

- Monitors log files from Vamsoft ORF email security product
- Automatically follows daily log file rotation (using date pattern in filename)
- Parses log entries and writes them to a PostgreSQL database
- Provides a WPF UI for configuration and monitoring
- Can run as a Windows Service or console application
- Implements robust error handling and fault tolerance
- Uses FileSystemWatcher for real-time file monitoring
- Performs batch processing for better performance

## Requirements

- .NET 6.0 or higher
- Windows 7/8/10/11 or Windows Server
- PostgreSQL 9.5 or higher

## Usage

### Installation

1. Download the latest release
2. Configure your PostgreSQL connection in `appsettings.json`
3. Run the application with `--install` parameter to install as a Windows Service:
   ```
   orf2Postgres.exe --install
   ```

### Running Modes

- **Windows Service**: Install using `--install` parameter and start from Services or the UI
- **UI Mode**: Run without parameters to launch the WPF interface
- **Console Mode**: Run with `--console` parameter to run in console mode
- **Uninstall**: Run with `--uninstall` parameter to remove the Windows Service

### Configuration

Configuration can be done via:

1. The WPF UI interface
2. Editing the `appsettings.json` file

The main configuration options are:

- **Log Directory**: Where to look for log files
- **Log Pattern**: Pattern of log files (default: `orfee-{Date:yyyy-MM-dd}.log`)
- **Polling Interval**: How often to check for new log entries (in seconds)
- **PostgreSQL Connection**: Database connection details
- **Table Name**: Name of the PostgreSQL table to write to

## Database Schema

The application creates a PostgreSQL table with the following schema:

```sql
CREATE TABLE orf_logs (
  id SERIAL PRIMARY KEY,
  message_id TEXT,
  event_source TEXT,
  event_datetime TIMESTAMPTZ,
  event_class TEXT,
  event_severity TEXT,
  event_action TEXT,
  filtering_point TEXT,
  ip TEXT,
  sender TEXT,
  recipients TEXT,
  msg_subject TEXT,
  msg_author TEXT,
  remote_peer TEXT,
  source_ip TEXT,
  country TEXT,
  event_msg TEXT,
  filename TEXT,
  processed_at TIMESTAMPTZ DEFAULT NOW()
);
```

## Troubleshooting

- **Service doesn't start**: Check Windows Event Viewer for error messages
- **Database errors**: Verify PostgreSQL connection and permissions
- **Log file not detected**: Check path and file pattern in configuration 