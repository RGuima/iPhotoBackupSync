# iPhoto Backup Sync

A Windows desktop app that compares an **origin** folder (e.g. your local
iCloud Photos folder) against a **destination** backup folder (local disk or
NAS), and shows every file, folder, and subfolder that exists in the origin
but is missing from the destination.

Built for large photo libraries: comparison is by path/structure only (file
contents are never read or hashed), directory listings are streamed with
bounded parallelism, and the UI list is virtualized -- so it stays responsive
across tens of thousands of files.

## Features

- **Folder comparison** -- recursively finds files/folders present in the
  origin but absent from the destination, and shows totals (missing file
  count, missing size) per folder.
- **iCloud sync status** -- for every missing file, shows one of four states
  using the Windows Cloud Filter API (the same mechanism iCloud for Windows,
  OneDrive, and Dropbox use for on-demand files):
  - **Refreshing** -- currently being uploaded/downloaded/hydrated
  - **Synced** -- fully backed up to iCloud
  - **Not synced** -- local changes not yet uploaded, or not a cloud file
  - **Error** -- status could not be determined
- **Selectable results** with a checkbox tree (tri-state: select a whole
  folder or individual files) and a right-click menu.
- **Actions** on the current selection:
  - Copy selected files to the destination (preserving relative folder
    structure; never modifies or deletes anything in the origin)
  - Export the full missing-file list to CSV
  - Reveal a file in File Explorer / copy its full path
- Works with local paths and NAS/UNC paths (`\\server\share\...`) for both
  origin and destination.

## Project layout

```
src/
  iPhotoBackupSync.Core/    Comparison engine, cloud-sync-status detection, file actions (no UI)
  iPhotoBackupSync.App/     WPF (.NET 8) desktop app
  iPhotoBackupSync.Tests/   xUnit tests for the comparison engine
```

## Building & running

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
on Windows.

```
dotnet build iPhotoBackupSync.sln
dotnet run --project src/iPhotoBackupSync.App/iPhotoBackupSync.App.csproj
```

Run the tests with:

```
dotnet test src/iPhotoBackupSync.Tests/iPhotoBackupSync.Tests.csproj
```

## How the comparison works

1. The destination folder is indexed once into an in-memory set of relative
   paths (files and folders), using bounded-parallel directory enumeration
   so network folders on a NAS don't pay round-trip latency serially.
2. The origin folder is then walked the same way. Any file or folder whose
   relative path isn't in the destination index is kept; directories that
   exist on both sides are only kept in the results if they contain a
   missing descendant, so the result tree stays as small as possible.
3. Cloud sync status is looked up only for files that are actually missing
   (not the whole origin tree), which keeps the relatively expensive native
   call count proportional to the size of the backup gap, not the size of
   the library.

## Notes & limitations

- **iCloud sync status is a best-effort heuristic.** The Cloud Filter API
  exposes a point-in-time placeholder state (in-sync / partially-on-disk /
  placeholder), not a live "% complete" for an in-flight transfer, so
  "Refreshing" is inferred from a partially-hydrated, not-yet-in-sync state
  rather than guaranteed to reflect an active transfer.
- Comparison is by **path and structure only** -- it does not compare file
  contents, size, or modified date to detect changes to a file that exists
  on both sides. That keeps scans fast on very large libraries, but it also
  means a file that was modified in the origin after being backed up will
  *not* show up as different.
- The "Copy Selected to Destination" action copies files; it never deletes
  or overwrites files in the origin, and it will prompt before creating a
  destination folder that doesn't exist yet.
