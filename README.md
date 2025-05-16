# orf2Postgres

A Windows service that monitors log files and imports them into a PostgreSQL database. The service watches for log files matching a specified pattern and automatically imports new entries into a configured PostgreSQL database table.

## Features

- Windows Service integration
- Real-time log file monitoring
- PostgreSQL database integration
- Configurable log file patterns
- Offset tracking to prevent duplicate imports
- Named pipe communication for status updates
- Robust error handling and logging

## Configuration

The service can be configured through `appsettings.json` with the following settings:

- `LogDirectory`: Directory to monitor for log files
- `LogFilePattern`: Pattern for log files to process (e.g., "orfee-{Date:yyyy-MM-dd}.log")
- `PollingIntervalSeconds`: How often to check for new log entries
- `OffsetFilePath`: Path to store file processing offsets
- Database connection settings:
  - `Host`: PostgreSQL server hostname
  - `Port`: PostgreSQL server port (default: 5432)
  - `Database`: Database name
  - `Username`: Database user
  - `Password`: Database password
  - `Schema`: Database schema (default: orf)
  - `TableName`: Table name (default: orf_logs)

## Database Schema

The service creates and uses the following database schema:

```sql
CREATE SCHEMA IF NOT EXISTS orf;

CREATE TABLE IF NOT EXISTS orf.orf_logs (
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

CREATE INDEX IF NOT EXISTS idx_orf_logs_event_datetime ON orf.orf_logs(event_datetime);
CREATE INDEX IF NOT EXISTS idx_orf_logs_filename ON orf.orf_logs(filename);
```

## Installation

1. Build the project
2. Run the executable with administrator privileges to install as a Windows Service
3. Configure the service through `appsettings.json`
4. Start the service through Windows Service Manager or the application UI

## Development

This project is built with:
- .NET 8.0
- Windows Forms for UI
- Npgsql for PostgreSQL connectivity 