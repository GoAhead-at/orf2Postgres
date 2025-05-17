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