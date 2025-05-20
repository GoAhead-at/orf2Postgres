# Project: ORF Log File Watcher with PostgreSQL Ingestion and Daily File Rotation

## 💡 Overview

The system is composed of two integrated parts in a single executable:
1. A **.NET 8 Background Service** that monitors ORF log files for new content and writes parsed log entries to a PostgreSQL database.
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
    - The service (typically running as `NetworkService`) hosts a Named Pipe Server.
    - The UI (running as a standard user) connects as a Named Pipe Client.
    - Access control for the pipe is crucial; the pipe is created using `System.IO.Pipes.NamedPipeServerStreamAcl.Create()` with a `PipeSecurity` object that grants `Authenticated Users` (including the standard user running the UI) necessary permissions (`ReadWrite` and `CreateNewInstance`) to connect to the service pipe. This ensures communication even when the UI is run by a non-administrator.
    - Commands and status updates are transmitted as JSON messages.
- **Mode-Independent Optimizations:** The same efficiency optimizations for offset tracking, duplicate prevention, and database operations apply across all modes:
    - Windows Service mode retains all optimizations when run as a background process
    - UI-embedded mode directly implements the same optimizations 
    - Service-UI communication reduces noise by filtering out redundant status messages when no data is processed
- **Unified Behavior Across Modes:** The application standardizes behavior between window and service modes:
    - Shared core processing logic extracted into common components
    - Consistent logging format and behavior via a centralized LogHelper
    - Identical file processing semantics regardless of execution mode
    - Minimal mode-specific code limited to system integration concerns
- **User Context Handling:** The application addresses different user contexts between service and window modes:
    - Configures file system permissions appropriately for service context
    - Uses environment-aware path resolution for configuration files
    - Implements proper elevation handling for administrative operations
    - Includes graceful degradation when permissions differ between contexts

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
- **Polling Efficiency:** The application optimizes system resources:
  - Avoids database operations when no new content is detected
  - In both service and UI modes, skips log reporting when no meaningful work is performed
  - When running as a Windows Service, minimizes IPC communication when no data needs to be processed
- **Date-Based File Handling:** The application properly handles date patterns in log file names:
  - Properly formats date patterns like `orfee-{Date:yyyy-MM-dd}.log` to exact current date
  - First attempts to find the exact matching file for the current date
  - Falls back to wildcard pattern search only when necessary
  - Ensures consistent behavior between service mode and UI mode

### 📌 Offset Persistence

- The application persists **byte offset per log file** in a local JSON file.
- On service startup:
  - It reads the current log file's name.
  - Loads its last known offset (if available), defaulting to offset `0` if missing.
  - Begins reading from that offset.
- **Atomicity:** Offset writing uses a temporary file approach for safety during unexpected shutdowns.
- **Optimization:** The application compares file sizes with stored offsets to skip processing when no new content exists.
- **Duplicate Prevention:** Multiple mechanisms prevent duplicate processing:
  - Byte offset tracking for efficient file reading
  - Fast file size comparison to avoid unnecessary processing attempts
  - Database-level entry hashing with unique constraints for absolute data integrity
  - Silent skipping of empty processing cycles to reduce log noise

### 🛢️ PostgreSQL Integration

- Uses the `Npgsql` library to insert log lines into PostgreSQL.
- **Configuration:** Loads connection parameters and application settings from `appsettings.json`:
  - This is the single source of truth for settings
  - The UI directly updates this file when settings change
  - No separate config files are maintained
- The log lines are parsed and inserted into a structured database table with the following schema:

  ```sql
  CREATE TABLE orf_logs (
    message_id TEXT PRIMARY KEY,
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

- Each line is inserted with source filename and processing timestamp.
- **Deduplication:** Using message_id as the primary key with `ON CONFLICT DO NOTHING` to automatically prevent duplicate entries.

### 🧯 Fault Tolerance

- The application handles various error conditions gracefully:
  - Missing log directories (with auto-creation attempts)
  - File access issues
  - Database connection failures
  - Malformed log lines
  - Service disconnections between UI and background service
  - User-friendly error messages instead of raw exceptions

### 🔒 Error Handling & Recovery Strategy

- **Categorized Exception Handling:**
  - **File System Errors:**
    - FileNotFoundException: Attempt creation or wait for file appearance with exponential backoff
    - UnauthorizedAccessException: Log detailed error, notify user of permission issues
    - IOException: Implement retry with progressive delays before giving up
  
  - **Database Errors:**
    - ConnectionException: Attempt reconnection with exponential backoff
    - CommandTimeoutException: Log warning, retry with increased timeout
    - ConstraintViolationException: Handle silently (expected for duplicates)
    - PostgresException: Parse error code, provide specific recovery for common codes
  
  - **Parsing Errors:**
    - FormatException: Log problematic line, continue processing
    - InvalidDataException: Skip malformed entries with warning
  
  - **Service Communication Errors:**
    - TimeoutException: Implement reconnection strategy
    - PipeException: Recreate pipe with fallback channels
  
- **Resilience Policies:**
  - Use Polly for implementing retry policies
  - Circuit breaker pattern for database operations
  - Jitter-based retry for file system operations
  - Different policies based on operation criticality
  
- **Graceful Degradation:**
  - Continue processing valid lines when encountering malformed entries
  - Fall back to local processing when service is unavailable
  - Queue operations when database is temporarily down
  
- **Recovery Procedures:**
  - Automatic service restart after critical failures
  - Transaction rollback and retry on database errors
  - Automatic offset recovery to prevent data loss
  - Self-healing mechanisms for known error conditions
  
- **Comprehensive Logging:**
  - Structured logging with context data
  - Log correlation IDs across components
  - Error severity classification
  - Integration with Windows Event Log for critical errors
  - Log rotation and retention policies

### ⚙️ Deployment Options

- **Windows Service:** The application can be installed and run as a Windows Service.
- **Console Application:** For development or headless server environments.
- **WPF Application:** For interactive use with UI.
- **In-Window Processing:** When running as a WPF application without the service installed, the UI can spawn its own processing instance.
- **Service Management & UAC:**
    - The UI can be run as a standard user.
    - **Installation/Uninstallation:** When the "Install Service" or "Uninstall Service" buttons are clicked, the application uses `sc.exe` with the `runas` verb. This will trigger a User Account Control (UAC) prompt, requesting administrator privileges for that specific operation only. The main UI application itself does not need to be launched as an administrator for these actions.
    - **Starting/Stopping Service:** The "Start Service" and "Stop Service" buttons in the UI currently interact with the service using `System.ServiceProcess.ServiceController`. These operations will only succeed if the UI application itself is launched with administrator privileges, as `ServiceController` does not have a built-in UAC elevation prompt mechanism for these actions. If the UI is run as a standard user, these buttons will typically be disabled or result in an error if clicked while targeting an installed service.

---

## 🖥️ User Interface Features

The WPF User Interface provides:

- **Configuration Management:**
  - **Database Connection Panel:**
    - Host/Server address input field
    - Port number input field with default (5432)
    - Username input field
    - Password input field (masked)
    - Database name input field
    - Schema input field with default (public)
    - Table name input field with default (orf_logs)
    - Connection timeout setting
    - Test Connection button
  
  - **Log File Configuration Panel:**
    - Base directory input field with directory browser button
    - Log file pattern input field with default (orfee-YYYY-MM-DD.log)
    - Pattern explanation/help text
    - Polling interval setting (in seconds)
    - Option to validate path existence
  
  - **Logging Configuration:**
    - Log level selector (dropdown with Status, Warning, Error options)
    - Log retention settings
    - UI log display settings (max entries, auto-scroll options)
  
  - **File Locations:**
    - Application settings (`appsettings.json`) stored in the application's directory
    - Offset tracking file stored in the application's directory
    - All file paths stored as absolute paths, not relative
    - No user-configurable locations for these files
    
  - **Action Buttons:**
    - Save Configuration button that writes all settings to appsettings.json
    - Test Connection button that verifies database connectivity
    - Verify Table button that checks if the table exists with correct structure
    - Create Table button that creates the table if it doesn't exist (with confirmation dialog)
    - Reset to Defaults button for resetting configuration

- **Service Control:**
  - Install/Uninstall buttons for Windows Service management (triggers UAC prompt if UI is run as standard user).
  - Start/Stop buttons for Windows Service (requires UI to be run as administrator to succeed) or local processing.
  - Enable/Disable buttons that control processing:
    - If service is installed: sends commands to the service (Start/Stop).
    - If no service is installed: runs processing directly in the application window (Start/Stop Processing).

- **Status Display:**
  - Current service status (Running, Stopped, Error)
  - Current file being processed and offset position
  - Detailed log view with operation information
  - Statistics panel showing processing counts and rates
  - Log level filter buttons (Status, Warning, Error) to filter the displayed logs

- **Database Tools:**
  - Test Connection button to verify PostgreSQL connectivity
  - Create/Verify Table button to ensure the required table exists:
    - Shows schema comparison if table exists but structure differs
    - Shows confirmation dialog before creating or altering table
  - View Sample Data button to show recent log entries
  - Database status indicator
  - Table statistics (row count, size)

- **UI Behavior:**
  - Changes to configuration are applied immediately after saving
  - Configuration is loaded from `appsettings.json` in the application directory
  - Settings are saved to the same `appsettings.json` file
  - All paths displayed and saved as absolute paths
  - Real-time validation of input fields
  - Confirmation dialogs for potentially destructive operations

---

## 🔧 Technical Implementation

- **Development Requirements:**
  - **.NET 8 SDK** for development and runtime
  - Visual Studio 2022 or equivalent IDE with .NET 8 support
  - PostgreSQL database (version 12.0 or higher recommended)

- **Leveraging Existing Libraries:**
  - **Database Access:** Npgsql for PostgreSQL connectivity
  - **Logging:** Microsoft.Extensions.Logging with Serilog or NLog as providers
  - **Configuration:** Microsoft.Extensions.Configuration with JSON provider
  - **Service Management:** Microsoft.Extensions.Hosting for Worker Service implementation
  - **IPC Communication:** H.Pipes or similar library for Named Pipes implementation. The `System.IO.Pipes.AccessControl` NuGet package is used for `NamedPipeServerStreamAcl.Create()`.
  - **UI Components:** Material Design in XAML Toolkit for modern UI elements
  - **Resilience:** Polly for retry policies and circuit breakers
  - **File Watching:** Enhanced FileSystemWatcher implementations or libraries
  - **Security:** Windows DPAPI via ProtectedData class for sensitive information
  - **Dependency Injection:** Microsoft.Extensions.DependencyInjection for service resolution

- **Configuration Management:**
  - Single configuration file `appsettings.json` for all settings
  - UI directly reads and writes to this file when settings change
  - Settings include database connection details, polling intervals, file patterns, etc.
  - Proper file locking implemented when writing to prevent corruption
  - Configuration changes trigger immediate application of new settings when appropriate

- **Error Handling:**
  - User-friendly error messages
  - Detailed logging
  - Graceful degradation when services are unavailable

- **Named Pipes Protocol:**
  - JSON-based message format with action/response pattern.
  - Bidirectional communication for commands and status updates.
  - To ensure standard users can communicate with the service (running as `NetworkService`), the pipe server is created using `System.IO.Pipes.NamedPipeServerStreamAcl.Create()`. This method, available via the `System.IO.Pipes.AccessControl` NuGet package (version 5.0.0 or compatible), allows a `PipeSecurity` object to be passed directly during the pipe's construction.
  - The `PipeSecurity` is configured to grant `PipeAccessRights.ReadWrite` and `PipeAccessRights.CreateNewInstance` to the `Authenticated Users` group (`WellKnownSidType.AuthenticatedUserSid`). This allows the UI, running as a standard user, to connect successfully.
  - Automatic reconnection attempts when service is available.

- **Multi-Mode Startup:**
  - Command-line arguments determine the application mode:
    - `--install` / `--uninstall`: Service installation management
    - `--console`: Run as console application
    - `--ui`: Run as WPF application (default for interactive sessions)
    - No arguments in non-interactive session: Run as Windows Service

- **Shared Core Logic:**
  - Core business logic extracted into shared components used by both service and UI modes
  - Common codebase for file reading, parsing, and database operations
  - Centralized logging implementation used consistently across all modes
  - File processing semantics identical between service and window modes
  - Shared configuration handling and validation

- **Encryption & Access Rights:**
  - Elevation (UAC) used when saving service-mode configurations
  - Windows DPAPI uses `DataProtectionScope.LocalMachine` (instead of `CurrentUser`) to ensure passwords encrypted by the UI (running as a user) can be decrypted by the service (potentially running as a different, e.g., system, account), ensuring service-compatible encryption.
  - Configuration marked as "service-ready" when prepared with system-level encryption
  - Graceful fallback for access rights differences between user and system contexts

- **Deployment Strategy:**
  - Published as a single-file executable (.exe) with all dependencies packaged
  - Self-contained deployment to eliminate external runtime dependencies
  - Support for different target platforms (x64, x86, ARM64 if necessary)
  - Minimal external dependencies requiring installation
  - **Example Publish Command (for win-x64):**
    ```bash
    dotnet publish "Log2Postgres.csproj" -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=false /p:IncludeNativeLibrariesForSelfExtract=true --output "release/Log2Postgres_win-x64_framework_dependent_singlefile"
    ```

### 🔐 Secure Windows Service Execution Context

- **Service Account Configuration:**
  - Run the service under a dedicated service account (not LocalSystem)
  - Create a least-privilege domain or local account specifically for the service
  - Avoid using personal user accounts or administrator accounts
  
- **Principle of Least Privilege:**
  - Grant only required permissions to the service account
  - Explicitly deny unnecessary access
  - Regular permission audits
  
- **File System Security:**
  - Set appropriate ACLs on log directories:
    - Read-only access to log files
    - Read/write access to application directory for configuration and offset files
    - Deny access to unrelated system areas
  - Use file system auditing to track access
  
- **Credential Management:**
  - Store database credentials securely using Windows DPAPI
  - Never store plaintext credentials in configuration files
  - Support for Windows Authentication to PostgreSQL when available
  - Consider integration with Azure Key Vault or similar for enterprise deployments
  
- **Network Security:**
  - Restrict outbound connections to only the database server
  - Configure proper firewall rules for the service
  - Consider using TLS for all database communications
  
- **IPC Security:**
  - Named pipes are secured to allow access for standard user UI processes connecting to the service running under the `NetworkService` account.
  - This is achieved by creating the `NamedPipeServerStream` using `NamedPipeServerStreamAcl.Create()`. A `PipeSecurity` object is passed during construction, configured to grant `PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance` to `Authenticated Users` (`WellKnownSidType.AuthenticatedUserSid`).
  - This ensures the UI can connect without requiring administrative privileges, establishing proper access control at the pipe's creation time by the `NetworkService`.
  - Message authentication for commands sent between UI and service (can be a future enhancement if needed).
  
- **Startup Parameters:**
  - Remove unnecessary permissions at service startup
  - Validate configuration integrity before execution
  - Verify execution environment meets security requirements
  
- **Service Hardening:**
  - Disable unnecessary Windows features in the service context
  - Regular security updates for the service host
  - Runtime verification of file integrity
  
- **Handling User Context Transitions:**
  - Securely transition between user context (UI) and service context
  - Elevation handling for administrative operations
  - Clear separation between privileged and unprivileged operations

---

## 📚 Helpful Documentation & Resources

| Topic                      | Resource                                                                 |
|---------------------------|--------------------------------------------------------------------------|
| Npgsql (PostgreSQL for .NET) | https://www.npgsql.org/                                                   |
| PostgreSQL Documentation  | https://www.postgresql.org/docs/                                        |
| FileStream in .NET        | https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream       |
| FileShare Options         | https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare        |
| .NET Worker Services      | https://learn.microsoft.com/en-us/dotnet/core/extensions/workers |
| Serilog                   | https://serilog.net/ |
| NLog                      | https://nlog-project.org/ |
| H.Pipes (Named Pipes)     | https://github.com/HavenDV/H.Pipes |
| Material Design in XAML   | http://materialdesigninxaml.net/ |
| Polly (Resilience Framework) | https://github.com/App-vNext/Polly |
| .NET Health Checks        | https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks |
| Windows DPAPI             | https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection |
| User Account Control (UAC) | https://learn.microsoft.com/en-us/windows/security/identity-protection/user-account-control/how-user-account-control-works |

