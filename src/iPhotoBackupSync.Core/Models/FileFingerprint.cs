using System.Text.RegularExpressions;

namespace iPhotoBackupSync.Core.Models;

/// <summary>
/// Identifies a file by size plus its name with any iCloud duplicate-suffix stripped --
/// rather than by the literal name, or by last-modified time.
///
/// iCloud renames files as part of its own sync/conflict resolution over time (observed in
/// practice: a plain name like "IMG_1596.HEIC" got reassigned to a completely different,
/// newer photo while the original was renamed to "IMG_1596(1).HEIC"), so matching by the
/// literal name can both wrongly treat a new file as already backed up (a stale
/// manifest/destination entry happens to sit under a name iCloud later reused) and wrongly
/// treat an already-backed-up file as missing after a harmless rename -- stripping the
/// "(N)" suffix survives that rename pattern while still keeping the rest of the name as a
/// signal.
///
/// Last-modified time was tried instead and found unusable in practice: sampling a real
/// 52,000+ file iCloud Photos library showed the vast majority of files' local last-write
/// time had drifted from what had been recorded months earlier -- by months or years, with
/// size staying byte-identical -- meaning iCloud rewrites this timestamp on its own when it
/// re-hydrates/re-touches a file, independent of the photo's actual content or capture
/// date. Size, by contrast, matched in effectively every sampled case.
/// </summary>
public readonly record struct FileFingerprint(long SizeBytes, string NormalizedName)
{
    private static readonly Regex DuplicateSuffix = new(@"\(\d+\)$", RegexOptions.Compiled);

    public static FileFingerprint FromFileName(string fileName, long sizeBytes) =>
        new(sizeBytes, Normalize(fileName));

    private static string Normalize(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var withoutSuffix = DuplicateSuffix.Replace(baseName, string.Empty);
        return (withoutSuffix + ext).ToUpperInvariant();
    }
}
