# Emanager Linux

A Linux desktop file manager built with C# / .NET 8 and Avalonia 11. This is the Linux port of [Emanager](https://github.com/Teknolojik-Adam/Emanager), maintained on the `linux` branch. The original Windows implementation remains on `master`.

## Features

- Dark/light UI, tabs, dual-pane layout, breadcrumbs and folder navigation
- File copy/move, rename, trash, session-based undo for supported operations
- Bulk rename with preview, drag and drop, file search
- Image thumbnails and basic preview
- Archive operations (Ark, 7z, tar), network locations via GVfs/GIO
- Open an external terminal in the current folder
- English interface (localization work in progress)

Some integrations depend on installed Linux desktop tools. Not all GUI interactions have been comprehensively tested.

## Build and run

Requires a Linux graphical session and the .NET 8 SDK.

```bash
dotnet restore src/Emanager.App/Emanager.App.csproj
dotnet build src/Emanager.App/Emanager.App.csproj -c Release
dotnet run --project src/Emanager.App/Emanager.App.csproj
```

Run file-operation smoke tests:

```bash
dotnet run --project tests/Emanager.FileOps.Smoke/Emanager.FileOps.Smoke.csproj -c Release
```

For local installation, publish and create a launcher for your environment; no system-wide package is currently supplied.

## Security

Do not commit credentials, tokens, private keys, personal settings or `.env` files. Use environment variables or a local ignored configuration for secrets. GitHub secret scanning is a secondary defense, not a substitute for checking commits before publishing.

## License

GNU Affero General Public License v3.0 (AGPL-3.0), consistent with the original project; see [LICENSE.txt](LICENSE.txt).
