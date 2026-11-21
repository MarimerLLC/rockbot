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
    /// The file-name stem (no extension) for <paramref name="sessionId"/>: <c>/</c> and
    /// <c>\</c> become <c>~</c>, other characters invalid in a file name become <c>_</c>.
    /// An id with neither is returned unchanged, so existing files keep their names.
    /// </summary>
    public static string StemFor(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return "_";

        if (sessionId.IndexOfAny(Invalid) < 0 && sessionId.IndexOf('/') < 0 && sessionId.IndexOf('\\') < 0)
            return sessionId;

        var chars = sessionId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '/' or '\\')
                chars[i] = '~';
            else if (Array.IndexOf(Invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }

    /// <summary>The full path of <paramref name="sessionId"/>'s <c>.jsonl</c> file under <paramref name="directory"/>.</summary>
    public static string JsonlPath(string directory, string sessionId) =>
        Path.Combine(directory, StemFor(sessionId) + ".jsonl");
}
