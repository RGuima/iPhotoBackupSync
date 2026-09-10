# iPhoto Backup Sync

A Windows desktop app that compares an **origin** folder (e.g. your local
iCloud Photos folder) against a **destination** backup folder (local disk or
NAS), and shows every file directly in the origin that's missing from the
destination.

Built for large photo libraries: comparison is by size plus name with any
iCloud duplicate suffix stripped (a file's *content fingerprint* -- bytes are
never read or hashed, and the literal name and last-modified time are never
trusted), and the UI list is virtualized -- so it stays responsive across
tens of thousands of files.

## Features

- **Folder comparison** -- top-level only: finds files present directly in
  the origin folder but absent directly in the destination folder, and shows
  totals (missing file count, missing size). Matching is by **size plus name
  with any `(N)` duplicate suffix stripped** -- not the literal name, and not
  last-modified time. iCloud renames files as part of its own sync/conflict
  resolution over time (observed in practice: a plain name like
  `IMG_1596.HEIC` got reassigned to a different, newer photo while the
  original became `IMG_1596(1).HEIC`), so matching by the literal name alone
  can both hide a genuinely-missing file behind a stale same-named entry and
  falsely re-flag an already-backed-up file that iCloud happened to rename.
  Last-modified time was tried too and abandoned: sampling a real 52,000+
  file library found iCloud rewrites a file's local timestamp by months or
  years, completely independent of its actual content, so date is not a
  usable signal for this at all (see Notes & limitations). Subfolders on
  either side are never descended into or compared -- both the real origin
  (an iCloud Photos folder) and this app's own copies are always flat, so a
  subfolder showing up at the destination belongs to something else entirely
  (in practice, a separate tool that reorganizes that same folder's contents
  by date after the fact) and is intentionally out of scope.
- **iCloud sync status** -- for every missing file, checks only the marker
  iCloud for Windows itself sets on a file (not the Windows Cloud Filter API
  reparse-point mechanism other providers like OneDrive use), so a folder
  that also happens to fall under a different provider's sync scope doesn't
  affect the result. Shows one of these states:
  - **Refreshing** -- currently being uploaded/downloaded/hydrated by iCloud
  - **Synced** -- fully backed up to iCloud
  - **Error** -- status could not be determined
- **Selectable results** with a checkbox list, Select All / Clear Selection,
  and a live selected-count-and-size summary. A right-click menu gives
  per-file actions.
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
  - Copy selected files to the destination root (never modifies or deletes
    anything in the origin). Only enabled once every selected file is fully
    synced with iCloud, so a partial/placeholder file never gets backed up --
    Copy re-runs Compare automatically afterward to refresh the list. If a
    destination file already occupies the origin file's name but has a
    different fingerprint (e.g. iCloud reused that name for different
    content), Copy never overwrites it -- it picks an unused
    `name (1).ext`-style name instead, the same way Windows Explorer would.
  - Export the full missing-file list to CSV
  - Reveal a file in File Explorer / copy its full path
- **Backup manifest**: a destination folder can hold a plain-text
  `iPhotoBackupSync.manifest.txt` file recording files that should count as
  already backed up even though the bytes themselves aren't (or are no
  longer) sitting in that folder -- for example, photos you copied out to an
  archive drive or optical media afterwards. Compare reads this file
  automatically and treats every fingerprint (size + de-suffixed name) it
  lists as present, regardless of what literal name is recorded alongside it.
  **Generate Manifest for Folder...** creates this file for any folder you
  pick (or updates it if one is already there), recording every file that
  currently exists directly in it (top-level only, same as Compare) --
  handy for stamping an existing archive as "already backed up" in one
  click. Regenerating only ever adds or refreshes entries; it never deletes
  one for a file the scan doesn't currently see, since that file may have
  since been moved to a different archive. Existing entries are matched by
  fingerprint (not name) and keep their original position in the file even
  when refreshed -- only the recorded name updates, in case iCloud renamed
  the file since the last scan -- and files new to a run are appended at the
  very end, preceded by a `# Added <timestamp>` marker for that batch, so
  the file reads as a rough history of when things were added. It's also
  marked Hidden on every write, and generation refuses to ever overwrite it
  with fewer entries than it already has -- both guard against other tools
  that scan a folder's visible contents (see Notes & limitations).
- The last-used origin and destination folders are remembered across
  sessions.
- Works with local paths and NAS/UNC paths (`\\server\share\...`) for both
  origin and destination.
- **Command-line mode** -- run `iPhotoBackupSync.exe <origin> <destination>`
  to drive the whole workflow unattended, with progress printed to the
  console (it attaches to the parent console if launched from one, e.g. a
  terminal or Task Scheduler, or opens its own console window otherwise):
  it compares the two folders, requests an iCloud sync (one file at a time,
  not in parallel) for every missing file that isn't already synced, then
  polls sync status until every missing file is synced or up to one hour
  has passed -- whichever comes first -- and copies whatever has become
  synced by then to the destination. The destination's backup manifest is
  always regenerated at the end of a command-line run, the same as clicking
  **Generate Manifest for Folder...** would -- this step is never skipped
  just because the run is unattended. Running with no arguments launches
  the normal desktop UI, as before.

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

1. The destination folder's top-level files are indexed into an in-memory
   set of content fingerprints (size + name with any `(N)` duplicate suffix
   stripped), plus every fingerprint recorded in the backup manifest, if any
   (subfolders are not descended into or indexed).
2. The origin folder's top-level files are then listed the same way. Any
   file whose fingerprint isn't in the destination index is missing; origin
   subfolders are never visited. Literal names and last-modified times are
   never compared -- only fingerprints.
3. Cloud sync status is looked up only for files that are actually missing
   (not the whole origin folder), which keeps the relatively expensive
   native call count proportional to the size of the backup gap, not the
   size of the library.

## Notes & limitations

- **iCloud sync status intentionally checks only iCloud's own marker, not the
  Windows Cloud Filter API reparse-point mechanism other providers use.**
  Sampling a real 52,000+ file "iCloud Photos" folder showed that iCloud for
  Windows does **not** use the Windows Cloud Filter API's reparse-point
  placeholders (`cldapi.dll`) at all -- every file came back
  `CF_PLACEHOLDER_STATE_NO_STATES`. Instead it marks files with the
  lighter-weight cloud-file *attribute* bits: a file with
  `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (or `RECALL_ON_OPEN`) isn't fully
  present on disk yet and would have to be fetched from iCloud to be opened,
  so it's shown as **Refreshing** (still downloading / queued); a file with
  neither attribute is treated as **Synced**. This app deliberately does
  *not* also check the Cloud Filter placeholder state that OneDrive (and
  some other providers) use -- so a file that happens to also sit somewhere
  OneDrive manages is judged purely on iCloud's own marker, never on what
  OneDrive thinks of it. Neither mechanism reports a live "% complete" for
  an in-flight transfer.
- Comparison is by **size + name with any `(N)` duplicate suffix stripped
  (a content fingerprint), top-level files only** -- the literal name and
  last-modified time are never compared, file bytes are never read/hashed,
  and it never looks inside subfolders on either side. Two things were tried
  and rejected before landing here:
  - **Matching by the literal name** meant the app could treat a brand-new,
    never-backed-up file as "already present" just because something else
    once occupied that exact name -- a real `IMG_1596.HEIC` got reassigned
    by iCloud to a new, different photo while the original file was renamed
    to `IMG_1596(1).HEIC`, and the stale entry under the old name masked the
    new file entirely.
  - **Adding last-modified time to the fingerprint** (size + date) was meant
    to fix that without depending on names at all, but sampling 25 random
    entries from a real 52,000+ file library after some time had passed
    found iCloud had rewritten **23 of the 25** files' local timestamps --
    by months or years in several cases -- while every file's size stayed
    byte-identical. Using date this way caused almost the entire
    already-backed-up library to look "missing" again and triggered a mass
    unnecessary re-sync attempt.

  The trade-off with size + de-suffixed name: a file placed in a subfolder
  on either side is invisible to Compare entirely (see the top-level-only
  note above), and two genuinely different files that happen to share both
  an exact byte size and the same name (ignoring an `(N)` suffix) would be
  mistaken for each other -- judged an acceptable risk given real photos
  essentially never collide on exact byte size, and it's far less likely
  than the failure modes of literal-name or date-based matching above.
- The "Copy Selected to Destination" action copies files straight into the
  destination root; it never deletes or overwrites files in the origin, it
  never overwrites a same-named destination file that turns out to be a
  different size, and it will prompt before creating a destination folder
  that doesn't exist yet.
- **The manifest is matched by fingerprint (size + de-suffixed name), not
  the literal name or last-modified time** -- the same signal Compare uses
  to detect a file's presence in the destination -- and (like Compare) it
  only ever records files directly in the destination root, not in
  subfolders. The recorded literal name is refreshed in place if iCloud
  renames the file later, but the fingerprint is the actual join key.
  Deleting a line, or the whole `iPhotoBackupSync.manifest.txt` file, makes
  that file show up as missing again on the next Compare.
- **Force Sync restarts the detected iCloud process(es)** (iCloud Photos /
  iCloud Drive / iCloud Services) and pins the target files (`attrib +P -U`),
  the same mechanism as the standalone "Cloud Sync Forcer" tool -- this is a
  deliberate, occasional action the user triggers, not something run
  automatically, since it briefly interrupts those processes system-wide.
