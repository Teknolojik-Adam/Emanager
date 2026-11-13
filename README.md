# Emanager for Linux

Emanager is a desktop file manager for Linux, built with C# and Avalonia UI. It provides a familiar interface for browsing files, organizing folders, and accessing local storage.

## Features

- Tabbed browsing and an optional dual-pane layout
- File and folder navigation with breadcrumbs, history, and search
- Copy, cut, paste, rename, and move-to-trash operations
- List, details, and icon views
- Quick access to common user folders and bookmarked locations
- Hidden-file visibility, sorting, and file properties
- Light and dark themes
- Storage information and mounted-volume navigation
- Network locations through GVfs (SMB and SFTP, where supported)

## Requirements

- Linux desktop environment (X11 or Wayland)
- .NET 8 SDK for building from source
- Network access to restore NuGet dependencies

## Build and Run

Clone the repository and switch to the Linux branch:

```bash
git clone --branch linux https://github.com/Teknolojik-Adam/Emanager.git
cd Emanager
dotnet restore src/Emanager.App/Emanager.App.csproj
dotnet build src/Emanager.App/Emanager.App.csproj -c Release
dotnet run --project src/Emanager.App/Emanager.App.csproj
```

## Network Locations

Network shares use the Linux GVfs/GIO infrastructure. Depending on your distribution, additional GVfs backends may be required.

Example addresses:

- SMB: `smb://server/share`
- SFTP: `sftp://user@server/`

On Fedora, you can install common GVfs components with:

```bash
sudo dnf install gvfs gvfs-smb gvfs-fuse
```

Do not include passwords in network URLs.

## Project Structure

- `src/Emanager.App/` — Linux desktop application
- `Emanager.sln` — solution file, if present in your checkout

## License

See [LICENSE.txt](LICENSE.txt) for the project's license terms.
