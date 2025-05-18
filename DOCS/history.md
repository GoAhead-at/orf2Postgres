# ORF Log File Watcher Project History

## Project Prompt for AI Context Recovery

You are assisting with developing a .NET 8 application that monitors ORF log files and ingests them into PostgreSQL. This is a dual-purpose application with both a background service and WPF UI component.

### Project Specifications

- **Project Name**: ORF Log File Watcher with PostgreSQL Ingestion
- **Platform**: .NET 8, Windows
- **Primary Components**:
  - Background Service (Windows Service/Console)
  - WPF User Interface
  - PostgreSQL Database Integration
- **Operating Modes**:
  - Windows Service
  - Console Application
  - WPF Desktop Application with processing capabilities

### Core Functionality

The application watches ORF log files that rotate daily (pattern: `orfee-YYYY-MM-DD.log`), parses log entries, and writes them to a PostgreSQL database. It maintains file reading offsets to avoid duplicate processing and supports seamless transitions between rotated files.

Key features include:
- File rotation handling at midnight
- Read-only file access with shared access mode
- Byte offset persistence in JSON file
- Fault tolerance and error recovery strategies
- Multiple deployment options
- Configuration management via WPF UI
- Named pipes communication between UI and service

### Database Schema

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

### Project Status

- Initial project setup completed
- Project description and requirements documented
- Implementation phase started
- Created history tracking file for context recovery
- Initial project structure created
- Basic application framework implemented
- User Interface implemented
- UI improved with proper input labels and consistent layout
- Core file watching service implemented
- Log file parsing logic implemented
- File position tracking implemented

### Development Steps

1. Project requirements and architecture defined
2. Repository initialized
3. Created history.md file for context recovery between chat sessions
4. Created single-executable WPF project structure
5. Added necessary NuGet packages:
   - Microsoft.Extensions.Hosting
   - Microsoft.Extensions.Hosting.WindowsServices
   - Npgsql
   - MaterialDesignThemes
   - Serilog.Extensions.Hosting
   - Serilog.Settings.Configuration
   - Serilog.Sinks.Console
   - Serilog.Sinks.File
   - System.ServiceProcess.ServiceController
   - Newtonsoft.Json
6. Created multi-mode application framework:
   - Set up Program.cs with mode detection (console/service/window)
   - Configured Host Builder with Windows Service support
   - Added Serilog integration
   - Created application configuration structure
   - Fixed build errors and made project buildable
   - Added basic folder organization
7. Implemented WPF User Interface:
   - Designed UI layout using Material Design
   - Created configuration panel with database and log file settings
   - Added service control buttons
   - Implemented log viewer with filtering
   - Created status display section
   - Added configuration save/load functionality
   - Implemented directory validation
   - Added placeholder for database functions
8. Enhanced User Interface:
   - Replaced placeholder hints with proper TextBlock labels for all input fields
   - Improved spacing and layout consistency across all form sections
   - Adjusted margins and alignment for better visual hierarchy
   - Enhanced overall accessibility by ensuring labels are always visible
9. Implemented Core File Watching Service:
   - Created OrfLogEntry model to represent parsed log entries
   - Implemented FilePosition model for tracking reading positions
   - Developed OrfLogParser service to parse ORF log files
   - Created PositionManager service to track and persist file positions
   - Built LogFileWatcher as a background service for file monitoring
   - Added event-based notification system for UI updates
   - Implemented file rotation handling with date pattern matching
   - Added fault tolerance with try/catch blocks and error logging
   - Fixed build errors and improved nullability handling

### Next Steps

- Implement PostgreSQL integration
- Connect the file watcher service to the UI
- Create service installation/management functionality
- Implement IPC between UI and service
- Add configuration management
- Implement error handling and recovery

### Development Standards

Following .NET development standards with:
- C# coding conventions
- Proper error handling and validation
- RESTful API design principles
- Performance optimization
- Security best practices 

## Session: YYYY-MM-DD (AI - Gemini) - Service Mode Implementation (Phase 1)

### Development Steps

10. **Service Mode Foundation & Password Encryption**:
    - Modified `PasswordEncryption.cs` to use `DataProtectionScope.LocalMachine` instead of `CurrentUser`. This ensures that database passwords encrypted by the UI (running under a user account) can be decrypted by the Windows Service (running under a system or dedicated service account) on the same machine.
    - Created `Core/Models/WindowsServiceSettings.cs` to define a simple class holding a `RunAsService` boolean flag. This helps the application determine its execution context.
    - Refactored `Core/Services/LogFileWatcher.cs`:
        - Added a constructor dependency for `IOptions<WindowsServiceSettings>`.
        - The `ExecuteAsync` method (from `BackgroundService`) now checks the `_isRunningAsHostedService` flag. If true, it automatically calls a new private method `StartProcessingAsyncInternal()` to begin log monitoring and processing.
        - The main work of `ExecuteAsync` is now awaiting `PollingLoopAsync`, which respects the `IsProcessing` flag.
        - Introduced new public methods `UIManagedStartProcessingAsync()` and `UIManagedStopProcessing()` specifically for the UI to control the `LogFileWatcher` when the application is *not* running as a hosted service (i.e., in UI/desktop mode). These methods include checks to prevent direct UI control if the app is in service mode (logging a warning, as IPC is not yet implemented).
        - The `UIManagedStartProcessingAsync()` method also ensures the `PollingLoopAsync` is started via `Task.Run` if the `LogFileWatcher` is not running as a hosted service (where `ExecuteAsync` would typically manage this).
        - The old `StartProcessingAsync()` was made private and renamed `StartProcessingAsyncInternal()`.
        - The old `StopProcessing()` logic was integrated into `UIManagedStopProcessing()` and the overridden `StopAsync(CancellationToken cancellationToken)`.
    - Updated `App.xaml.cs`:
        - Modified `CreateHostBuilder` to determine the run mode (service vs. UI) by checking for a `--windows-service` command-line argument.
        - Registered `WindowsServiceSettings` in the DI container, configured based on the determined run mode.
        - Ensured core services (`OrfLogParser`, `PositionManager`, `PostgresService`) are registered.
        - Implemented conditional registration for `LogFileWatcher`:
            - If `runAsService` is true, it's registered using `services.AddHostedService<LogFileWatcher>()`.
            - If `runAsService` is false, it's registered using `services.AddSingleton<LogFileWatcher>()`.
        - Added logging to indicate the application's startup mode.
        - Improved error handling in `OnStartup` to log critical errors to `startup_error.txt` and display more informative messages.

### Current Project Status (Post Service Mode - Phase 1)

- **Service Mode**: Foundational logic for running `LogFileWatcher` as a Windows Service is implemented. The service can auto-start its processing tasks.
- **Password Encryption**: Database password encryption now uses `LocalMachine` scope, compatible with service execution contexts.
- **Configuration**: DI is set up to differentiate between service and UI modes for `LogFileWatcher` instantiation and behavior.
- **Pending**:
    - Service installation/uninstallation functionality (e.g., via command-line arguments like `--install`, `--uninstall`).
    - IPC (Inter-Process Communication, e.g., Named Pipes) for the UI to communicate with and control the *actual running Windows Service* (start, stop, get status). Currently, UI control methods in `LogFileWatcher` are placeholders when in service mode.
    - `MainWindow.xaml.cs` needs to be updated to use the new `UIManagedStartProcessingAsync()` and `UIManagedStopProcessing()` methods of `LogFileWatcher` for in-window processing mode.
    - Comprehensive testing of the application running as an installed Windows Service.

### Next Steps (Planned)

- Update `MainWindow.xaml.cs` to correctly use `LogFileWatcher.UIManagedStartProcessingAsync()` and `LogFileWatcher.UIManagedStopProcessing()`.
- Implement service installation and uninstallation capabilities (e.g., command-line arguments `--install`/`--uninstall`).
- Implement IPC using Named Pipes for UI-to-service communication (status updates, commands).
- Thoroughly test service mode operations, including startup, shutdown, log processing, and error handling.
- Review and remove debug logging statements that expose sensitive information (e.g., passwords, entropy values) from `PasswordEncryption.cs` and other relevant files.

11. **UI Interaction Update for `LogFileWatcher`**:
    - Modified `MainWindow.xaml.cs` to align with the refactored `LogFileWatcher`:
        - The `StartProcessingAsync` helper method (called by `StartBtn_Click`) now calls `await _logFileWatcher.UIManagedStartProcessingAsync()`.
        - The `StopProcessing` helper method (called by `StopBtn_Click` and `MainWindow_Closing`) now calls `_logFileWatcher.UIManagedStopProcessing()`.
        - Management of the internal `_isProcessing` flag in `MainWindow.xaml.cs` is now more tightly coupled with the results of these calls and the state of `_logFileWatcher.IsProcessing`.
        - UI button states (`StartBtn`, `StopBtn`, and configuration buttons like `SaveConfigBtn`, `ResetConfigBtn`, `BrowseBtn`, `TestConnectionBtn`, `VerifyTableBtn`) are updated to reflect processing state (e.g., config buttons disabled during processing).
        - The `ReloadConfiguration` method was refactored:
            - Removed direct state manipulation of `LogFileWatcher`.
            - It now calls `await _logFileWatcher.UpdateSettingsAsync()`.
            - If UI-managed processing was active, it attempts a stop/start sequence using the new `UIManagedStopProcessing` and `UIManagedStartProcessingAsync` methods to ensure the watcher restarts cleanly with the new settings.

### Current Project Status (Post UI Update for Service Mode)

- **Service Mode**: Foundational logic for running `LogFileWatcher` as a Windows Service is implemented. The service can auto-start its processing tasks.
- **Password Encryption**: Database password encryption now uses `LocalMachine` scope, compatible with service execution contexts.
- **Configuration**: DI is set up to differentiate between service and UI modes for `LogFileWatcher` instantiation and behavior.
- **UI Interaction**: `MainWindow.xaml.cs` has been updated to use the new `UIManagedStartProcessingAsync` and `UIManagedStopProcessing` methods of `LogFileWatcher`, ensuring correct UI-driven control when not running as a service.
- **Pending**:
    - Service installation/uninstallation functionality (e.g., via command-line arguments like `--install`, `--uninstall`).
    - IPC (Inter-Process Communication, e.g., Named Pipes) for the UI to communicate with and control the *actual running Windows Service* (start, stop, get status).
    - Comprehensive testing of the application running as an installed Windows Service and UI-managed mode.

### Next Steps (Planned)

- Implement service installation and uninstallation capabilities (e.g., command-line arguments `--install`/`--uninstall` in `App.xaml.cs`).
- Implement IPC using Named Pipes for UI-to-service communication (status updates, commands).
- Thoroughly test service mode operations, including startup, shutdown, log processing, and error handling.
- Review and remove debug logging statements that expose sensitive information (e.g., passwords, entropy values) from `PasswordEncryption.cs` and other relevant files.

12. **Service Installation and Uninstallation**:
    - Modified `App.xaml.cs` to handle command-line arguments `--install` and `--uninstall`.
    - Added an `IsAdministrator()` helper method to check for necessary privileges.
    - Implemented `InstallService()` method:
        - Constructs and executes `sc.exe create Log2Postgres binPath= "path\to\YourApp.exe --windows-service" DisplayName= "Log2Postgres ORF Log Watcher" start= auto obj= LocalSystem`.
        - Includes logic to correctly determine the `binPath` for both self-contained (`.exe`) and framework-dependent (`dotnet YourApp.dll`) deployments, ensuring the `--windows-service` argument is passed to the executable when started by the SCM.
        - Provides console and `MessageBox` feedback on success or failure.
    - Implemented `UninstallService()` method:
        - Attempts to stop the service using `sc.exe stop Log2Postgres`.
        - Constructs and executes `sc.exe delete Log2Postgres`.
        - Provides console and `MessageBox` feedback.
    - Application now exits after install/uninstall operations if these arguments are detected, preventing the UI from launching.

### Current Project Status (Post Service Install/Uninstall Implementation)

- **Service Mode**: Foundational logic for running `LogFileWatcher` as a Windows Service is implemented. The service can auto-start its processing tasks.
- **Password Encryption**: Database password encryption now uses `LocalMachine` scope, compatible with service execution contexts.
- **Configuration**: DI is set up to differentiate between service and UI modes for `LogFichierWatcher` instantiation and behavior.
- **UI Interaction**: `MainWindow.xaml.cs` has been updated to use the new `UIManagedStartProcessingAsync` and `UIManagedStopProcessing` methods of `LogFileWatcher`, ensuring correct UI-driven control when not running as a service.
- **Service Management**: Basic command-line driven service installation (`--install`) and uninstallation (`--uninstall`) using `sc.exe` is implemented in `App.xaml.cs`. Requires administrator privileges.
- **Pending**:
    - Wiring up UI buttons ("Install Service", "Uninstall Service") to trigger these command-line operations (likely requiring elevation).
    - IPC (Inter-Process Communication, e.g., Named Pipes) for the UI to communicate with and control the *actual running Windows Service* (start, stop, get status).
    - Comprehensive testing of service installation, uninstallation, startup, operation, and uninstallation, including the `binPath` logic for different deployment types.

### Next Steps (Planned)

- Test service installation/uninstallation and runtime behavior thoroughly.
- Implement UI button functionality for service installation/uninstallation (including UAC elevation).
- Implement IPC using Named Pipes for UI-to-service communication. 

13. **Program.cs Refactoring for Service Commands & Successful Test**
    - **Issue Identified**: Running the application with `--install` caused a crash related to WPF initialization (`App.InitializeComponent()`). This was because `Program.Main` was unconditionally creating and initializing the WPF `App` object before checking for service-related command-line arguments.
    - **Refactoring `Program.cs`**:
        - Modified `Program.Main` to parse `args` for `--install` and `--uninstall` *before* any WPF `App` object is created or `InitializeComponent()` is called.
        - If these arguments are detected, `Program.Main` now directly calls the static methods `App.IsAdministrator()`, `App.InstallService()`, and `App.UninstallService()`.
        - `Environment.Exit()` is called after these operations to prevent the WPF application from starting.
    - **Refactoring `App.xaml.cs`**:
        - The `IsAdministrator()`, `InstallService()`, and `UninstallService()` methods were confirmed/made `public static` to be callable from `Program.Main` without an `App` instance.
        - The command-line argument parsing logic for `--install` and `--uninstall` was removed from the `App()` constructor as it's now handled by `Program.Main`.
    - **Testing**:
        - Successfully tested `Log2Postgres.exe --install`. The service was installed without WPF errors.
        - Successfully tested `Log2Postgres.exe --uninstall`. The service was uninstalled.
        - Successfully tested starting and stopping the installed service via `services.msc`.
    - **Outcome**: The application can now be correctly installed and uninstalled as a Windows service using command-line arguments without crashing due to premature WPF initialization. Windowed mode remains functional.

### Current Project Status (Post Program.cs Refactor & Service Test)

- **Service Mode**: Foundational logic for running `LogFileWatcher` as a Windows Service is implemented. The service can auto-start its processing tasks.
- **Password Encryption**: Database password encryption now uses `LocalMachine` scope.
- **Configuration**: DI differentiates between service and UI modes.
- **UI Interaction**: `MainWindow.xaml.cs` uses `UIManagedStartProcessingAsync` and `UIManagedStopProcessing`.
- **Service Management**: Command-line driven service installation (`--install`) and uninstallation (`--uninstall`) using `sc.exe` is implemented and functional, handled correctly in `Program.cs` before WPF initialization. The service can be started and stopped.
- **Pending**:
    - Wiring up UI buttons ("Install Service", "Uninstall Service") to trigger these command-line operations (likely requiring elevation).
    - IPC (Inter-Process Communication, e.g., Named Pipes) for the UI to communicate with and get status from/send commands to the *actual running Windows Service*.
    - Comprehensive testing of the *service's core functionality* (log watching, parsing, DB writing) while running as an installed service.

### Next Steps (Planned)

- Implement UI button functionality for service installation/uninstallation (including UAC elevation).
- Implement IPC using Named Pipes for UI-to-service communication (status updates, commands).
- Thoroughly test service mode operations, including startup, shutdown, log processing, and error handling.
- Review and remove debug logging statements that expose sensitive information (e.g., passwords, entropy values) from `PasswordEncryption.cs` and other relevant files. 

14. **UI Buttons for Service Installation/Uninstallation**:
    - Implemented click event handlers for `InstallServiceBtn_Click` and `UninstallServiceBtn_Click` in `MainWindow.xaml.cs`.
    - These handlers use `System.Diagnostics.Process.Start()` to re-launch the current application's executable (`Process.GetCurrentProcess().MainModule.FileName`).
    - The `ProcessStartInfo` is configured with:
        - `Arguments`: `"--install"` or `"--uninstall"`.
        - `Verb`: `"runas"` (to request UAC elevation for administrator privileges).
        - `UseShellExecute = true` (required for the `runas` verb).
    - Added error handling for scenarios such as UAC denial (Win32Exception with NativeErrorCode 1223) and other exceptions during process startup.
    - User feedback is provided via `UpdateStatusBar` and `MessageBox` dialogs.
    - This allows users to install or uninstall the Windows service directly from the UI, triggering the UAC prompt for necessary permissions.

### Current Project Status (Post UI Service Management Buttons)

- **Service Mode**: Foundational logic for running `LogFileWatcher` as a Windows Service is implemented.
- **Password Encryption**: Uses `LocalMachine` scope.
- **Configuration**: DI differentiates between service and UI modes.
- **UI Interaction**: `MainWindow.xaml.cs` uses `UIManagedStartProcessingAsync` and `UIManagedStopProcessing` for in-app processing.
- **Service Management (CLI)**: Command-line installation/uninstallation (`--install`/`--uninstall`) is functional and correctly handled in `Program.cs`.
- **Service Management (UI)**: UI buttons ("Install Service", "Uninstall Service") in `MainWindow.xaml.cs` are now implemented to trigger the CLI commands with UAC elevation.
- **Pending**:
    - IPC (Inter-Process Communication, e.g., Named Pipes) for the UI to communicate with and get status from/send commands to the *actual running Windows Service*.
    - Comprehensive testing of the *service's core functionality* (log watching, parsing, DB writing) while running as an installed service.
    - UI adaptation based on service status (e.g., disabling local start/stop if service is active).

### Next Steps (Planned)

- Implement IPC using Named Pipes for UI-to-service communication (status updates, commands).
- Adapt UI based on service status (e.g., disable local controls if service is active, display service status).
- Thoroughly test service mode operations, including startup, shutdown, log processing, and error handling.
- Review and remove debug logging statements that expose sensitive information. 

15. **IPC - Named Pipe Server in Service (`LogFileWatcher.cs`)**:
    - Added `using System.IO.Pipes;`, `using System.Text;`, and `using Newtonsoft.Json;` to `LogFileWatcher.cs`.
    - Defined a constant `PipeName = "Log2PostgresServicePipe"`.
    - In `ExecuteAsync`, when `_isRunningAsHostedService` is true, a new task (`_ipcServerTask`) is started to run `RunIpcServerAsync`.
        - This task uses its own `CancellationTokenSource` (`_ipcServerCts`) for independent control.
    - Implemented `private async Task RunIpcServerAsync(CancellationToken cancellationToken)`:
        - Creates a `NamedPipeServerStream` with the defined `PipeName`.
        - Enters a loop (until cancellation) to `WaitForConnectionAsync()`.
        - Upon client connection, reads a request string. If the request is `"GET_STATUS"`:
            - Constructs a status object containing `ServiceOperationalState` (derived from `IsProcessing` and `_lastProcessingError`), `IsProcessing`, `CurrentFile`, `CurrentPosition`, `TotalLinesProcessedSinceStart`, and `LastErrorMessage` (a new private field `_lastProcessingError` was added and is set in `NotifyError`).
            - Serializes this status object to JSON using `Newtonsoft.Json`.
            - Writes the JSON response back to the client pipe.
        - Handles client disconnection and various exceptions robustly within the loop.
    - Modified `StopAsync` to cancel `_ipcServerCts` and wait for `_ipcServerTask` to complete, ensuring a graceful shutdown of the IPC server.
    - The `NotifyError` method now updates `_lastProcessingError` to make error information available via IPC.

### Current Project Status (Post IPC Server Implementation)

- **Service Mode**: Foundational logic for running `LogFileWatcher` as a Windows Service is implemented.
- **Password Encryption**: Uses `LocalMachine` scope.
- **Configuration**: DI differentiates between service and UI modes.
- **UI Interaction**: `MainWindow.xaml.cs` uses `UIManagedStartProcessingAsync` and `UIManagedStopProcessing` for in-app processing.
- **Service Management (CLI & UI)**: Service installation/uninstallation is functional via command-line and UI buttons (with UAC).
- **IPC (Service-Side)**: `LogFileWatcher.cs` now hosts a Named Pipe server (`Log2PostgresServicePipe`) when running as a service. It responds to `"GET_STATUS"` requests with a JSON payload containing the service's operational status and processing details.
- **Pending**:
    - IPC (UI-Side): Implement the Named Pipe client in `MainWindow.xaml.cs` to connect to the service, query its status, and update the UI.
    - UI adaptation based on service status from IPC.
    - Comprehensive testing of the entire service lifecycle and IPC mechanism.

### Next Steps (Planned)

- Implement Named Pipe client in `MainWindow.xaml.cs` for status querying.
- Implement a timer in `MainWindow.xaml.cs` to periodically query service status via IPC.
- Update UI elements in `MainWindow.xaml.cs` based on the status received from the service (e.g., disable local start/stop, show service file/position).
- Thoroughly test IPC communication, service operations, and UI responsiveness. 

16. **IPC - Named Pipe Client in UI (`MainWindow.xaml.cs`)**:
    - Added `using System.IO.Pipes;`, `System.Text;`, `Newtonsoft.Json;`, and `System.Windows.Threading;` to `MainWindow.xaml.cs`.
    - Defined `private const string PipeName = "Log2PostgresServicePipe";`.
    - Added a private class `PipeServiceStatus` to deserialize the JSON response from the service.
    - Implemented a `DispatcherTimer _serviceStatusTimer`:
        - Initialized in `MainWindow_Loaded`, set to tick every 5 seconds.
        - The `Tick` event handler (`ServiceStatusTimer_Tick`) calls `QueryServiceStatusAsync`.
        - Timer is started in `MainWindow_Loaded` and stopped in `MainWindow_Closing`.
    - Implemented `private async Task QueryServiceStatusAsync()`:
        - Creates a `NamedPipeClientStream` to connect to the service's pipe (`.` for localhost, `PipeName`).
        - Attempts to connect with a 2-second timeout (`ConnectAsync(2000)`).
        - If connected, sends the `"GET_STATUS"` command (UTF8 encoded).
        - Reads the JSON response from the pipe (increased buffer to 4096 bytes).
        - Deserializes the JSON into a `PipeServiceStatus` object.
        - Calls `UpdateUiWithServiceStatus(statusObject, serviceAvailableFlag)` on the dispatcher thread.
        - Handles `TimeoutException` (service pipe not available) and other `IOExceptions` or general exceptions by setting `serviceAvailableFlag` to false.
    - Implemented `private void UpdateUiWithServiceStatus(PipeServiceStatus status, bool serviceAvailable)`:
        - If `serviceAvailable` is false or `status` is null:
            - Updates UI to indicate "Service Not Available" (e.g., `ServiceStatusText`, `ServiceStatusIndicator`).
            - Clears service-specific data fields (e.g., `ServiceCurrentFileText` - assuming such XAML elements will be added for service data).
            - Enables local UI processing controls (`StartBtn`, `StopBtn`, config buttons) based on the UI's `_isProcessing` state.
        - If `status` is available:
            - Updates `ServiceStatusIndicator`, `ServiceStatusText`, and `ProcessingStatusText` based on `status.ServiceOperationalState`, `status.IsProcessing`, and `status.LastErrorMessage`.
            - Updates main processing display fields (`CurrentFileText`, `CurrentPositionText`, `LinesProcessedText`, `LastErrorText`) with data from the service, possibly prefixing or indicating it's from the service.
            - **Button Logic**: If `status.IsProcessing` is true or `status.ServiceOperationalState == "Running"`:
                - Disables `StartBtn`, `StopBtn` (UI doesn't directly control service start/stop via these).
                - Disables `SaveConfigBtn`, `ResetConfigBtn` (as service is using the current configuration).
                - Sets tooltips on these buttons to explain why they are disabled (e.g., "Processing is managed by the Windows Service").
            - Else (service not actively processing or unavailable):
                - Enables `StartBtn`, `StopBtn`, and config buttons, respecting the local `_isProcessing` flag for UI-driven mode.
                - Clears service-related tooltips.
    - An initial call to `QueryServiceStatusAsync` is made in `MainWindow_Loaded`.

### Current Project Status (Post IPC Client Implementation)

- **Service Management (CLI & UI)**: Fully functional.
- **IPC (Service-Side)**: `LogFileWatcher.cs` hosts a Named Pipe server, responds to `"GET_STATUS"`.
- **IPC (UI-Side)**: `MainWindow.xaml.cs` now has a Named Pipe client that periodically queries the service for status using a `DispatcherTimer`. It updates UI elements (status indicators, text fields) and disables/enables local processing controls (`StartBtn`, `StopBtn`, config buttons) based on the reported service status.
- **Pending**:
    - Addition of dedicated XAML elements in `MainWindow.xaml` to display service-specific details clearly (e.g., `ServiceCurrentFileText`, `ServiceCurrentPositionText`, etc.) to avoid confusion with local processing UI elements if they are distinct.
    - Comprehensive testing of the entire service lifecycle, IPC mechanism, and UI responsiveness under various scenarios (service running, service stopped, service processing, service error).

### Next Steps (Planned)

- Add dedicated XAML elements for service status display if desired.
- Thoroughly test IPC communication, service operations, and UI responsiveness.
- Review and remove any remaining debug logging statements, especially those exposing sensitive information. 

17. **IPC - Real-time Service Configuration Update Refinement (Post Initial IPC)**
    - **Symptom**: After the initial IPC implementation for status (`GET_STATUS`), a problem was identified where, if the `Log2Postgres` Windows Service was running and its configuration was updated via the UI (e.g., Log Directory changed) and saved, the service would not always apply these new settings in real-time. The UI would show the service as still being in an error state (e.g., "Log directory not configured") even after settings were saved and an IPC `UPDATE_SETTINGS` command was intended to be sent.
    - **Investigation & Debugging**:
        - Added `UPDATE_SETTINGS:<json_payload>` IPC command to `LogFileWatcher.cs`'s named pipe server. The payload is a JSON representation of `LogMonitorSettings`.
        - `MainWindow.xaml.cs` was updated: `SaveSettings()` now calls `SendUpdateSettingsToServiceAsync()` if the service is running. `SendUpdateSettingsToServiceAsync()` sends the `UPDATE_SETTINGS` command.
        - `LogFileWatcher.RunIpcServerAsync` was updated to handle `UPDATE_SETTINGS`, deserialize the settings, and call its internal `UpdateSettingsAsync(LogMonitorSettings newSettings)`.
        - **Key Finding**: Despite these IPC additions, the service often failed to update. Detailed UI debug logs (`log-*.txt`) revealed a `System.InvalidOperationException: The calling thread cannot access this object because a different thread owns it.`
        - This exception occurred within `MainWindow.xaml.cs` in the `ReloadConfiguration` method (specifically when it called methods like `LogMessage` or `LogError` that directly update UI elements).
        - `SaveSettings()` calls `ReloadConfiguration` and then `SendUpdateSettingsToServiceAsync` from within a `Task.Run(...)` block. The cross-thread UI access exception in `ReloadConfiguration` was terminating this background task prematurely, *before* `SendUpdateSettingsToServiceAsync` could be executed. Thus, the IPC message to the service was never sent.
    - **Solution Implemented**:
        - Made the UI helper methods in `MainWindow.xaml.cs` (`LogMessage`, `LogWarning`, `LogError`, `UpdateStatusBar`) thread-safe.
        - Each of these methods now checks `if (!Dispatcher.CheckAccess())` and, if true, uses `Dispatcher.Invoke(() => ...)` to marshal the UI update logic back to the main UI thread.
        ```csharp
        // Example for LogMessage
        private void LogMessage(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => LogMessage(message));
                return;
            }
            // ... original UI update logic ...
        }
        ```
    - **Outcome**:
        - The cross-thread exception in `ReloadConfiguration` was resolved.
        - The `Task.Run` block in `SaveSettings` now completes successfully, ensuring that `SendUpdateSettingsToServiceAsync` is called.
        - The `LogFileWatcher` service now correctly receives the `UPDATE_SETTINGS` IPC command and applies the new configuration in real-time, resolving the error state as expected.

### Current Project Status (Post IPC Service Configuration Update Fix)

- **Service Management (CLI & UI)**: Fully functional.
- **IPC (Service-Side)**: `LogFileWatcher.cs` hosts a Named Pipe server, responds to `"GET_STATUS"` and `"UPDATE_SETTINGS"` commands.
- **IPC (UI-Side)**: `MainWindow.xaml.cs` periodically queries service status and sends settings updates via IPC. UI controls are dynamically updated based on service state.
- **Real-time Configuration**: The service now reliably updates its configuration in real-time when settings are changed in the UI and the service is running.
- **Pending**:
    - Comprehensive testing of all features, including various edge cases for service and UI interaction.
    - Final review and removal of any remaining non-essential debug logging, especially those exposing sensitive information.

### Next Steps (Planned)

- Perform thorough end-to-end testing of the application in all modes (UI-managed, Service).
- Review logging levels and ensure sensitive information is not logged in release configurations.
- Consider adding more IPC commands if further service control from the UI is desired (e.g., force rescan, clear error state). 