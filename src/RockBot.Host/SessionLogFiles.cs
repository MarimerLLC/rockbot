using System.Text;

namespace RockBot.Host;

/// <summary>
/// Maps a session id to the file-name stem a per-session JSONL log uses. Session ids
/// can hold a path separator: every scheduled-task run is <c>patrol/{task}</c>. Using
/// the raw id as a file name put those files in a <c>patrol/</c> subdirectory nobody
/// created, so every append threw and the fire-and-forget writer dropped it (#848).
/// The stem keeps the directory flat, so the top-level enumeration and retention
/// helpers see every session's file.
/// </summary>
public static class SessionLogFiles
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>
    /// The file-name stem (no extension) for <paramref name="sessionId"/>. <c>/</c> becomes
    /// <c>~</c>, which keeps <c>patrol/{task}</c> readable as <c>patrol~{task}</c>. <c>~</c>,
    /// <c>%</c>, <c>\</c> and any character invalid in a file name become <c>%XX</c> (their
    /// UTF-16 code in hex), so no two session ids share a stem, and with it a file and a
    /// write lock. An id with none of these is returned unchanged, so existing files keep
    /// their names.
    /// </summary>
    public static string StemFor(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return "%00";

        if (!sessionId.Any(NeedsMapping))
            return sessionId;

        var sb = new StringBuilder(sessionId.Length + 8);
        foreach (var c in sessionId)
        {
            if (c == '/')
                sb.Append('~');
            else if (NeedsMapping(c))
                // Every mapped character is ASCII, so two hex digits always suffice.
                sb.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool NeedsMapping(char c) =>
        c is '/' or '\\' or '~' or '%' || Array.IndexOf(Invalid, c) >= 0;

    /// <summary>The full path of <paramref name="sessionId"/>'s <c>.jsonl</c> file under <paramref name="directory"/>.</summary>
    public static string JsonlPath(string directory, string sessionId) =>
        Path.Combine(directory, StemFor(sessionId) + ".jsonl");
}
