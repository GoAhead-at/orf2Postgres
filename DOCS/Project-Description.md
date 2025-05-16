# Project: ORF Log File Watcher with PostgreSQL Ingestion and Daily File Rotation

## 💡 Overview

The system is composed of two integrated parts in a single executable:
1. A **.NET Background Service** that monitors ORF log files for new content and writes parsed log entries to a PostgreSQL database.
2. A **WPF User Interface** for configuring, monitoring, and controlling the background service.

The application can run in three modes:
- As a Windows Service
- As a console application
- As a WPF desktop application with integrated processing capabilities

The log file is **rotated daily**, using the current date in its filename (e.g., `orfee-YYYY-MM-DD.log`). The service avoids duplicate processing by tracking the last-read byte offset per file and supports seamless transitions between rotated files. The UI communicates with the service via Named Pipes.

---

## 🏛️ Architecture

- **Multi-Mode Application:** The application runs in one of three modes (Windows Service, Console, or WPF UI).
- **Integrated Service:** The core file processing logic resides in a .NET Worker Service, designed to run either as a Windows Service or within the UI process.
- **WPF UI:** The User Interface uses Windows Presentation Foundation (WPF) for configuration and monitoring.
- **Communication (IPC):** The UI and service communicate using Named Pipes:
    - The service hosts a Named Pipe Server
    - The UI connects as a Named Pipe Client
    - Commands and status updates are transmitted as JSON messages
    - The UI can operate in standalone mode when no service is detected

---

## ✅ Functional Requirements

### 🔁 File Rotation

- Log file changes daily and follows the pattern: `orfee-YYYY-MM-DD.log`.
- At midnight (or upon detecting a new file), the service:
  - Finalizes reading from the previous file.
  - Opens the new day's file and starts from byte offset `0`.

### 📂 File Access

- Files are opened in **read-only mode with shared access** to avoid conflicts with the writing process.
- **Resource Management:** All resources (FileStream, Database connections) are properly disposed using `using` statements.

### 🔄 Polling & File Watching

- A combination of polling and FileSystemWatcher is used to detect file changes:
  - FileSystemWatcher provides immediate notifications of file creation/changes
  - Polling provides a backup mechanism and handles offset management
  - This hybrid approach ensures reliable log monitoring

### 📌 Offset Persistence

- The application persists **byte offset per log file** in a local JSON file.
- On service startup:
  - It reads the current log file's name.
  - Loads its last known offset (if available), defaulting to offset `0` if missing.
  - Begins reading from that offset.
- **Atomicity:** Offset writing uses a temporary file approach for safety during unexpected shutdowns.

### 🛢️ PostgreSQL Integration

- Uses the `Npgsql` library to insert log lines into PostgreSQL.
- **Configuration:** Loads connection parameters and application settings from both:
  - `appsettings.json` (application-wide defaults)
  - Local `config.json` (user-specific settings saved from the UI)
- The log lines are parsed and inserted into a structured database table with the following schema:

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

- Each line is inserted with source filename and processing timestamp information.

### 🧯 Fault Tolerance

- The application handles various error conditions gracefully:
  - Missing log directories (with auto-creation attempts)
  - File access issues
  - Database connection failures
  - Malformed log lines
  - Service disconnections between UI and background service
  - User-friendly error messages instead of raw exceptions

### ⚙️ Deployment Options

- **Windows Service:** The application can be installed and run as a Windows Service.
- **Console Application:** For development or headless server environments.
- **WPF Application:** For interactive use with UI.
- **In-Window Processing:** When running as a WPF application without the service installed, the UI can spawn its own processing instance.

---

## 🖥️ User Interface Features

The WPF User Interface provides:

- **Configuration Management:**
  - Input fields for PostgreSQL connection details (host, port, credentials, database, schema, table)
  - Settings for log file directory, file pattern, and polling interval
  - Configuration persists between application restarts
  - Configuration loads from appsettings.json by default

- **Service Control:**
  - Install/Uninstall buttons for Windows Service management
  - Enable/Disable buttons that control processing:
    - If service is installed: sends commands to the service
    - If no service is installed: runs processing directly in the application window

- **Status Display:**
  - Current service status (Running, Stopped, Error)
  - Current file being processed and offset position
  - Detailed log view with operation information

- **Database Tools:**
  - Test Connection button to verify PostgreSQL connectivity
  - Create/Verify Table button to ensure the required table exists

---

## 🔧 Technical Implementation

- **Configuration Hierarchy:**
  1. Application first loads from `appsettings.json`
  2. Then checks for local `config.json` (user settings)
  3. Falls back to hardcoded defaults as a last resort

- **Error Handling:**
  - User-friendly error messages
  - Detailed logging
  - Graceful degradation when services are unavailable

- **Named Pipes Protocol:**
  - JSON-based message format with action/response pattern
  - Bidirectional communication for commands and status updates
  - Automatic reconnection attempts when service is available

- **Multi-Mode Startup:**
  - Command-line arguments determine the application mode:
    - `--install` / `--uninstall`: Service installation management
    - `--console`: Run as console application
    - `--ui`: Run as WPF application (default for interactive sessions)
    - No arguments in non-interactive session: Run as Windows Service

---

## 📚 Helpful Documentation & Resources

| Topic                      | Resource                                                                 |
|---------------------------|--------------------------------------------------------------------------|
| Npgsql (PostgreSQL for .NET) | https://www.npgsql.org/                                                   |
| PostgreSQL Documentation  | https://www.postgresql.org/docs/                                        |
| FileStream in .NET        | https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream       |
| FileShare Options         | https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare        |
| BackgroundWorker / HostedService in .NET | https://learn.microsoft.com/en-us/dotnet/core/extensions/workers |
| Best Practices for File Tailing | https://loggly.com/blog/engineering-log-file-tail-scalable-reliable/   |
| Detecting Inode/File Change (Linux) | https://man7.org/linux/man-pages/man2/stat.2.html                   |
| JSON Persistence in .NET  | https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-overview |
| .NET Configuration        | https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration |
| .NET Logging              | https://learn.microsoft.com/en-us/dotnet/core/extensions/logging |
| Polly (Resilience Framework) | https://github.com/App-vNext/Polly |
| .NET Health Checks        | https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks |

