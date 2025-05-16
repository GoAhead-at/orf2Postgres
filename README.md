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
    timestamp TIMESTAMP WITH TIME ZONE,
    log_level VARCHAR(50),
    source VARCHAR(255),
    message TEXT,
    file_name VARCHAR(255),
    file_offset BIGINT,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_orf_logs_timestamp ON orf.orf_logs(timestamp);
CREATE INDEX IF NOT EXISTS idx_orf_logs_file_name ON orf.orf_logs(file_name);
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