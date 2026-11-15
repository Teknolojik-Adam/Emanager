# 🚀 EManager — Windows & Linux File Manager

**EManager** is an open-source desktop file manager developed in C# to make file and folder management easier.

Starting with Windows, the project has evolved with its new Linux version into a comprehensive application that brings together multi-tab file management, Android phone connectivity, advanced file operations, network access, and storage management.

## ✨ Key Features

### 📁 Modern File Management
- Multiple tab support
- Dual-pane file explorer
- Copying, moving, deleting, and renaming files and folders
- Drag-and-drop and clipboard operations
- Undo file operations
- Folder history and quick navigation
- Pinning frequently used folders
- Viewing file properties
- Showing hidden files
- Sorting files by name, type, size, and date

### 🎨 User Interface
- Light and dark themes
- Turkish and English language support
- Customizable keyboard shortcuts
- Details view, list, and large icon modes
- Image thumbnails and file previews
- Right-click context menus
- Integrated terminal access

### 📱 Android Phone Management
- Automatic detection of Android MTP devices over USB
- Browsing Android folders
- Listing and searching phone files
- Downloading files from phone to computer
- Sending files from computer to phone
- Viewing transfer progress and speed
- Android file explorer integrated into the main interface
- Device and transfer queue panels

### 🔄 File Transfer Center
- Managing file transfer operations from a central screen
- Transfer percentage and speed tracking
- Estimated time remaining information
- Pausing and resuming local transfer operations
- Canceling transfers
- Offering user choices on file conflicts
- Viewing the status of completed and failed operations

### 🔍 Advanced File Search and Analysis
- Searching by file name
- Searching in subfolders
- Text search within file contents
- Filtering by MIME type
- Detecting duplicate files
- SHA-256 and SHA-512 calculation
- Organizing files with tags
- Folder comparison and synchronization

### 📝 Batch Renaming
- Renaming multiple files at once
- Automatic sequence numbering
- Find and replace
- Adding prefixes and suffixes
- Preserving file extensions
- Previewing new names before the operation

### 📦 Archive Management
- Creating ZIP archives
- Creating 7z archives
- Creating TAR.GZ archives
- Ark integration
- Opening and extracting archives
- Previewing ZIP contents

### 💾 Disk and USB Management
- Viewing physical disks and partitions
- Inspecting disk capacity and file system information
- Mounting and unmounting storage devices
- Safely removing USB devices
- Protection policies for system disks
- UDisks2 integration

### 🌐 Network File Management
- Connecting to SMB network shares
- SFTP connections
- FTP and FTPS support
- WebDAV connections
- Saving network locations
- Browsing files in connected network locations
- GVfs/GIO integration

### 🗑️ Trash
- Viewing deleted files
- Restoring files to their original location
- Permanently deleting selected files
- Emptying the trash
- Viewing files' previous locations

### 🔐 System and Permission Management
- Editing Linux file permissions
- KIO-Admin and Polkit integration
- File operations with administrator privileges
- Closing admin sessions and timeout management
- Protection against system-critical disk operations

## 🖥️ Platforms and Technologies

| Platform | Technology |
|---|---|
| Windows | C# · Windows Forms · .NET Framework 4.7.2 |
| Linux | C# · .NET 8 · Avalonia UI |
| Linux file and device access | GVfs · GIO · UDisks2 |
| Linux data management | SQLite |
| Windows logging | NLog |

## 🪟 Windows Version

The Windows version of EManager offers a practical desktop experience for everyday file management.

**Main features:**
- File and folder browsing
- Copy, cut, paste, and rename
- File search with wildcard support
- Light and dark themes
- Remembering the last used folder
- Keyboard shortcuts
- Terminal integration
- NLog logging infrastructure

The source code for the Windows version is available in this GitHub repository.

## 🐧 Linux Version

EManager Linux is the next-generation desktop version, developed and run on Fedora.

This version transforms the traditional file explorer into an advanced workspace. Android file access, network connections, USB management, file analysis, the transfer center, and dual-pane browsing are combined in a single application.

The Linux version is built with .NET 8 and Avalonia UI.

*Once the source code for the Linux version is added to the GitHub repository, this section will be expanded with installation and build instructions.*

## 🛠️ Building the Windows Version

```bash
git clone https://github.com/Teknolojik-Adam/Emanager.git
```

Open the `Emanager.sln` file in the repository with Visual Studio, install the required NuGet dependencies, and build the project.

## 📄 License

EManager is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**.

You can review the full license in the [LICENSE.txt](LICENSE.txt) file.

---

**EManager — Manage your files, devices, and transfers from one place.**
