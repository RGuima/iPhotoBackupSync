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
  folder or individual files), Select All / Clear Selection, and a live
  selected-count-and-size summary. A right-click menu gives per-file actions.
- **Filter and sort**: filter the results by name, last-modified date range,
  and/or cloud sync status (any combination) -- combine a sync-status filter
  with Select All to select e.g. "everything not yet synced" in one click.
  Click a column header to sort by it (click again to reverse); Shift+click
  another header to add it as a secondary/tertiary sort level.
- **Force Sync**: for selected files that aren't fully synced yet, asks
  iCloud to sync them right now instead of waiting for its own schedule
  (restarts the iCloud client process(es) and pins the files). Refresh Sync
  Status re-checks status afterward without a full re-compare.
- **Actions** on the current selection:
  - Copy selected files to the destination (preserving relative folder
    structure; never modifies or deletes anything in the origin). Only
    enabled once every selected file is fully synced with iCloud, so a
    partial/placeholder file never gets backed up -- Copy re-runs Compare
    automatically afterward to refresh the list.
  - Export the full missing-file list to CSV
  - Reveal a file in File Explorer / copy its full path
- **Backup manifest**: a destination folder can hold a plain-text
  `iPhotoBackupSync.manifest.txt` file recording files that should count as
  already backed up even though the bytes themselves aren't (or are no
  longer) sitting in that folder -- for example, photos you copied out to an
  archive drive or optical media afterwards. Compare reads this file
  automatically and treats every relative path it lists (and the file's
  parent folders) as present. **Generate Manifest for Folder...** creates or
  overwrites this file for any folder you pick, recording every file that
  currently exists in it (relative path, size, last-modified date) -- handy
  for stamping an existing archive as "already backed up" in one click.
- The last-used origin and destination folders are remembered across
  sessions.
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

- **iCloud sync status uses two different OS signals, chosen based on what a
  real iCloud Photos folder actually reports.** Sampling a real 52,000+ file
  "iCloud Photos" folder showed that iCloud for Windows does **not** use the
  Windows Cloud Filter API's reparse-point placeholders (`cldapi.dll`) at all
  -- every file came back `CF_PLACEHOLDER_STATE_NO_STATES`. Instead it marks
  files with the lighter-weight cloud-file *attribute* bits: a file with
  `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (or `RECALL_ON_OPEN`) isn't fully
  present on disk yet and would have to be fetched from the cloud to be
  opened, so it's shown as **Refreshing** (still downloading / queued) even
  though the same file might separately show as "in sync" at the metadata
  level -- local content completeness wins over that metadata bit. A file
  with neither of those attributes nor any placeholder markers is treated as
  **Synced**, since cloud providers mark a file that still needs to be
  uploaded, or isn't fully downloaded, immediately -- the absence of any such
  marker is the normal state for a file that's already finished syncing. The
  Cloud Filter placeholder path is still checked first and used when present,
  so a provider that *does* use reparse-point placeholders (OneDrive, for
  example) is handled by that path instead. Neither mechanism reports a live
  "% complete" for an in-flight transfer.
- Comparison is by **path and structure only** -- it does not compare file
  contents, size, or modified date to detect changes to a file that exists
  on both sides. That keeps scans fast on very large libraries, but it also
  means a file that was modified in the origin after being backed up will
  *not* show up as different.
- The "Copy Selected to Destination" action copies files; it never deletes
  or overwrites files in the origin, and it will prompt before creating a
  destination folder that doesn't exist yet.
- **The manifest is matched by relative path only**, the same signal used to
  detect a file's presence in the destination generally -- it does not
  record or check file contents, size, or modified date for equality (the
  size/date columns are written for human reference only). Deleting a line,
  or the whole `iPhotoBackupSync.manifest.txt` file, makes those files show
  up as missing again on the next Compare.
- **Force Sync restarts the detected iCloud process(es)** (iCloud Photos /
  iCloud Drive / iCloud Services) and pins the target files (`attrib +P -U`),
  the same mechanism as the standalone "Cloud Sync Forcer" tool -- this is a
  deliberate, occasional action the user triggers, not something run
  automatically, since it briefly interrupts those processes system-wide.
