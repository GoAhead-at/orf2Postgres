# ORF Log File Watcher with PostgreSQL Ingestion

A .NET 8 application that monitors ORF log files and ingests them into PostgreSQL. This is a dual-purpose application with both a background service and WPF UI component.

## Features

- Monitors ORF log files for new entries, processing them in near real-time.
- Handles daily log file rotation (e.g., `orfee-YYYY-MM-DD.log`) seamlessly.
- Parses log entries into a structured format for database ingestion.
- Stores processed log entries in a PostgreSQL database.
- Maintains file reading positions (byte offsets) to prevent duplicate processing, even after restarts.
- Supports multiple operational modes:
    - Windows Service: Runs as a background service for continuous monitoring.
    - WPF Desktop Application: Provides a UI for configuration, status monitoring, and manual processing control.
- User-friendly WPF UI for:
    - Configuring database connection parameters (host, port, credentials, etc.).
    - Setting log monitoring options (directory, file pattern, polling interval).
    - Viewing live processing status (current file, position, lines processed, errors).
    - Displaying application logs with filtering (INFO, WARNING, ERROR) and a 500-line limit.
    - Managing the Windows Service (install, uninstall, start, stop) with UAC elevation for required operations.
    - Testing database connectivity and verifying table structure.
- Securely stores database passwords using Windows DPAPI (LocalMachine scope for service compatibility).
- Inter-Process Communication (IPC) using Named Pipes allows the WPF UI to communicate with the running Windows Service for status updates and configuration changes.

## Development Status

- ✅ Core file watching and parsing logic implemented.
- ✅ File position tracking and persistence implemented.
- ✅ PostgreSQL integration for log storage completed.
- ✅ WPF User Interface for configuration and monitoring implemented.
- ✅ Windows Service installation, uninstallation, and control (start/stop) via UI and command-line arguments implemented.
- ✅ IPC (Named Pipes) for UI-to-Service communication (status and settings updates) implemented.
- ✅ Secure password encryption using Windows DPAPI implemented.
- ✅ UI logging with filtering and line limiting implemented.
- 🏁 All major features are complete; ongoing work focuses on minor bug fixes and stability improvements.

## Getting Started

### Prerequisites

- .NET 8.0 SDK (or .NET 8.0 Runtime if using pre-built executable)
- PostgreSQL Server (version 12+ recommended)
- Windows 10/11 or Windows Server

### Building the Project

```bash
dotnet build -c Release
```

### Running the Application

The application is typically run by launching the executable (e.g., `Log2Postgres.exe` found in `src/Log2Postgres/bin/Release/net8.0-windows/`).

- **WPF UI Mode (Default)**: Double-clicking the executable or running it without specific arguments will start the WPF application.
  ```bash
  Log2Postgres.exe
  ```

- **Windows Service Management (Command Line)**:
  These commands require Administrator privileges. The application will internally handle UAC prompting if necessary when launched via UI buttons, or you can run your command prompt as Administrator.

  - **Install Service**:
    ```bash
    Log2Postgres.exe --install
    ```
    This will register the application as a Windows Service named "Log2Postgres" set to start automatically.

  - **Uninstall Service**:
    ```bash
    Log2Postgres.exe --uninstall
    ```
    This will stop and remove the "Log2Postgres" Windows Service.

  - **Note on Service Startup**: Once installed, the service is typically managed via the Windows Services console (`services.msc`) or the UI's Start/Stop Service buttons. The `--windows-service` argument is used internally by the Service Control Manager (SCM) to launch the executable as a service and should not be used directly by the user for starting the service.

## License

This project is licensed under the MIT License. See the [LICENSE.md](LICENSE.md) file for details.

## Acknowledgments

- Vamsoft ([https://vamsoft.com/](https://vamsoft.com/)) as the vendor of the ORF software whose logs this application processes.

## Development Notes

### Debugging Hidden Processes

If the application runs but doesn't show a window (indicating a potential startup crash before the UI can initialize):
- Check for a `startup_crash.txt` file in the application's main directory (where the executable is located). This file logs critical errors that occur very early during startup.
- Also, check the application's `logs` subfolder (within the main application directory) for more detailed diagnostic logs created by Serilog, if the application managed to initialize its logging system before failing.
- Ensure there are no file locks or permission issues preventing the application from writing to its directory or accessing necessary resources.

### Error Handling & Logging
- The application uses Serilog for robust logging to files (typically in a `logs` subfolder within the application directory) and to the UI log panel.
- Critical startup errors are logged to `startup_crash.txt` in the application's base directory.
- The UI provides feedback for common operations and errors.

### UI Log Behavior
- The UI log panel displays INFO, WARNING, and ERROR level messages from the application.
- Toggle buttons allow filtering the displayed messages by level.
- The log display is limited to the most recent 500 lines to maintain performance.

### Configuration
- Application settings are stored in `appsettings.json` in the application directory.
- The UI allows modification of these settings, which are applied to the local processing instance and can be sent to a running service via IPC.

### File Position Tracking
- Byte offsets for processed log files are stored in `positions.json` in the application directory.
- This ensures that processing resumes from the correct point after restarts, preventing data duplication.
- The application handles the creation of this file if it doesn't exist. 