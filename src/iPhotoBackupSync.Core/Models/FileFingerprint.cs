namespace iPhotoBackupSync.Core.Models;

/// <summary>
/// Identifies a file by its content signature -- size plus last-modified time -- rather
/// than by name. iCloud renames files as part of its own sync/conflict resolution over
/// time (observed in practice: a plain name like "IMG_1596.HEIC" got reassigned to a
/// completely different, newer photo while the original was renamed to
/// "IMG_1596(1).HEIC"), so matching by name alone can both wrongly treat a new file as
/// already backed up (a stale manifest/destination entry happens to sit under a name
/// iCloud later reused) and wrongly treat an already-backed-up file as missing after a
/// harmless rename. Size + last-modified time is a much more stable proxy for "is this
/// the same file", without the cost of actually hashing file contents.
/// </summary>
public readonly record struct FileFingerprint(long SizeBytes, DateTime? LastModifiedUtc);
