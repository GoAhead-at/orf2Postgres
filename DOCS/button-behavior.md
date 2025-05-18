# Log2Postgres Button Behavior Documentation

This document outlines the behavior and underlying logic of each button in the Log2Postgres application.

## Configuration Buttons

### Browse Button
- **Behavior**: 
  - Opens a folder browser dialog to select a log file directory
  - Lists all log files (*.log) in the selected directory
  - If no pattern is set and log files exist, automatically sets pattern based on first file found
- **Logic**:
  - Uses `System.Windows.Forms.FolderBrowserDialog` to present a directory selection dialog
  - If a directory is already specified, uses that as the initial path
  - Updates the `LogDirectory.Text` property with the selected path
  - Calls `Directory.GetFiles()` to scan for *.log files in the directory
  - If `LogFilePattern.Text` is empty and files are found, sets pattern to match first file
  - Displays found files in the log (up to 5 files to avoid log spam)
  - No validation is performed at this stage

### Save Config Button
- **Behavior**: 
  - Saves all current configuration settings to the `appsettings.json` file
  - Disabled when processing is active, with tooltip explaining why
- **Logic**:
  - Loads existing `appsettings.json` file
  - Updates all configuration sections with current UI values
  - Encrypts the database password before saving
  - Writes the updated configuration back to disk
  - Calls `ReloadConfiguration()` which:
    - Creates a `LogMonitorSettings` object with current UI values
    - Validates that the log directory exists and is readable
    - Creates the directory if missing (with a warning message)
    - Checks for log files and issues warning if none found
    - Synchronizes the processing state between UI and LogFileWatcher
    - Updates the LogFileWatcher with new settings
    - Only processes existing files if IsProcessing is already true
  - When processing is active (`_isProcessing = true`):
    - Button is disabled
    - Tooltip shows "Stop processing to enable configuration changes"

### Reset Config Button
- **Behavior**: 
  - Resets all configuration fields to default values
  - Disabled when processing is active, with tooltip explaining why
- **Logic**:
  - Displays a confirmation dialog before proceeding
  - If confirmed, sets all UI fields to predefined default values:
    - Database host: "localhost"
    - Database port: "5432"
    - Database name: "postgres"
    - Database schema: "public"
    - Database table: "orf_logs"
    - Log file pattern: "orfee-{Date:yyyy-MM-dd}.log"
    - Polling interval: "5" seconds
  - Does NOT automatically save these defaults to disk (requires clicking Save Config)
  - When processing is active (`_isProcessing = true`):
    - Button is disabled
    - Tooltip shows "Stop processing to enable configuration changes"

## Database Buttons

### Test Connection Button
- **Behavior**: Tests the PostgreSQL database connection with current settings
- **Logic**:
  - Creates a temporary `DatabaseSettings` object with current UI values
  - Instantiates a new `PostgresService` with these settings
  - Calls `TestConnectionAsync()` to verify the connection
  - Updates the UI with status (success or failure)
  - On failure, attempts to extract detailed error information from log files

### Verify Table Button
- **Behavior**: 
  - Verifies if the required database table exists and creates it if missing after confirmation
  - Checks table structure and offers to recreate if structure doesn't match requirements
- **Logic**:
  - Creates a temporary `DatabaseSettings` object with current UI values
  - Tests the database connection first
  - If connected, calls `TableExistsAsync()` to check if the table exists
  - If table doesn't exist:
    - Asks user for confirmation to create it
    - If confirmed, calls `CreateTableAsync()` to create the table
  - If table exists:
    - Calls `ValidateTableStructureAsync()` to check if all required columns exist
    - If structure doesn't match:
      - Shows confirmation dialog warning that data will be lost
      - If confirmed, calls `DropTableAsync()` followed by `CreateTableAsync()`
  - Updates UI with operation status
  - On success, calls `GetRowCountAsync()` to display current record count

## File Processing Buttons

### Start Button
- **Behavior**: Starts the log file processing operation
- **Logic**:
  - Validates that the log directory exists and is readable
  - If directory doesn't exist or isn't readable, logs error and doesn't start
  - Sets `_isProcessing = true` to prevent duplicate starts
  - Updates UI to show processing status
  - Checks if settings have changed since last start
  - If settings changed, calls `ReloadConfiguration()` to update
  - **IMPORTANT**: Explicitly sets LogFileWatcher's processing state to true
  - Calls `LogFileWatcher.StartProcessingAsync()` to begin processing
  - Starts a timer to periodically update the position display
  - Error handling:
    - Resets processing state on failure
    - Ensures LogFileWatcher state is synchronized

### Stop Button
- **Behavior**: Stops the log file processing operation
- **Logic**:
  - **IMPORTANT**: Sets UI state (`_isProcessing = false`) first
  - Updates UI to show stopped status
  - **IMPORTANT**: Explicitly sets LogFileWatcher's processing state to false
  - Calls `LogFileWatcher.StopProcessing()` to stop processing
  - Stops the position update timer
  - Resets processing status UI elements

## Service Management Buttons

### Install Service Button
- **Behavior**: Would install application as a Windows Service
- **Logic**:
  - Currently only shows a "Not Implemented" message
  - Future implementation would:
    - Save current configuration
    - Register the application as a Windows Service
    - Configure service startup parameters

### Uninstall Service Button
- **Behavior**: Would uninstall the Windows Service
- **Logic**:
  - Currently only shows a "Not Implemented" message
  - Future implementation would:
    - Stop the service if running
    - Unregister the service from Windows

## Log Management Buttons

### Log Filter Toggle Button
- **Behavior**: Would filter log entries by type
- **Logic**:
  - Currently not fully implemented
  - Future implementation would filter by log level (Info, Warning, Error)

### Clear Logs Button
- **Behavior**: Clears the log text display area
- **Logic**:
  - Calls `LogTextBox.Clear()` to remove all text
  - Adds a single message indicating logs were cleared

## Automatic Path Validation

Rather than having a dedicated button, path validation happens automatically:

- **When Configuration is Saved**:
  - The log directory is checked for existence and readability
  - If it doesn't exist, it's created automatically (with a warning message)
  - Logs a warning if no log files are found in the directory
  
- **When Processing Starts**:
  - The log directory is validated to ensure it exists and is accessible
  - If the directory doesn't exist or isn't readable, processing won't start
  - The application checks for log files and logs a warning if none are found

This automatic validation replaces the manual validation button, making the interface cleaner and ensuring validation happens at critical moments.

## State Synchronization Logic

One critical aspect of the application is maintaining proper state synchronization between the UI (`_isProcessing` flag) and the LogFileWatcher service (`IsProcessing` property).

### Key Synchronization Points:
1. **ReloadConfiguration** - Explicitly checks and synchronizes states:
   ```csharp
   bool currentWatcherState = _logFileWatcher.GetProcessingState();
   if (currentWatcherState != _isProcessing)
   {
       _logFileWatcher.SetProcessingState(_isProcessing);
   }
   ```

2. **StartProcessingAsync** - Ensures proper state before starting:
   ```csharp
   _isProcessing = true;
   _logFileWatcher.SetProcessingState(true);
   await _logFileWatcher.StartProcessingAsync();
   ```

3. **StopProcessing** - Ensures proper state before stopping:
   ```csharp
   _isProcessing = false;
   _logFileWatcher.SetProcessingState(false);
   _logFileWatcher.StopProcessing();
   ```

This explicit synchronization prevents issues where configuration changes might trigger unwanted file processing when the UI indicates processing is stopped. 