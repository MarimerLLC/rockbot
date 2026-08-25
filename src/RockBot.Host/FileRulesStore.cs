using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-backed implementation of <see cref="IRulesStore"/>, storing rules as markdown
/// bullets in <c>rules.md</c> in the agent profile directory.
/// Thread-safe via an async semaphore.
/// </summary>
/// <remarks>
/// <para>
/// The file is treated as a document, not as a serialised list. Every line that is not a
/// bullet — headings, prose, blank lines, a note explaining why a rule exists — is preserved
/// verbatim in place, and a bullet keeps its original marker and indentation. Reading rules
/// out and regenerating the file from them destroyed anything a human had written there on
/// the next <c>add_rule</c>, which is exactly the kind of loss that goes unnoticed.
/// </para>
/// <para>
/// Every mutation re-reads the file first. There is no watcher on <c>rules.md</c>, so a copy
/// pushed to a running pod would otherwise be overwritten by the next write from a list
/// loaded at startup. The file is a handful of lines; re-reading it costs nothing.
/// </para>
/// <para>
/// <see cref="Rules"/> is likewise a read-through, gated on the file's timestamp and length so
/// an unchanged file costs one <c>stat</c> per prompt rather than a parse. Serving a startup
/// snapshot there was the other half of the same bug: a <c>rules.md</c> pushed to a running pod
/// reached the prompt only after a restart, while the profile documents beside it hot-reload in
/// about half a second. A rule the operator can see on disk but the agent is not yet following
/// is the worst of both.
/// </para>
/// <para>
/// A watcher would be the obvious alternative and is deliberately not used: these files live on
/// a Longhorn PVC, where watchers have been observed to miss the rename half of an atomic write
/// while polling catches it. A stat on read has no such blind spot, needs no background loop,
/// and cannot observe a half-written file — <see cref="AtomicFile"/> replaces content by rename,
/// so a reader sees the old bytes or the new ones and never a mixture.
/// </para>
/// </remarks>
internal sealed partial class FileRulesStore : IRulesStore
{
    private readonly string _filePath;
    private readonly ILogger<FileRulesStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Cache of the rule texts, refreshed from disk on every read-through and every write.
    /// <see cref="Rules"/> is consumed synchronously while building each system prompt, so it
    /// serves this snapshot rather than touching the filesystem per turn.
    /// </summary>
    private IReadOnlyList<string> _rules;

    /// <summary>
    /// Timestamp and length of the file as last parsed, or <see cref="Missing"/> when there was
    /// no file. Compared on every <see cref="Rules"/> read to decide whether a re-parse is
    /// needed. <c>null</c> only before the first successful stat.
    /// </summary>
    private (DateTime LastWriteUtc, long Length)? _stamp;

    /// <summary>
    /// Guards the read-through refresh. Separate from <see cref="_lock"/>, which is async and
    /// held across writes; <see cref="Rules"/> is read synchronously while a prompt is being
    /// assembled and cannot wait on a semaphore.
    /// </summary>
    private readonly object _refreshGate = new();

    public FileRulesStore(IOptions<AgentProfileOptions> options, ILogger<FileRulesStore> logger)
    {
        _logger = logger;

        var opts = options.Value;
        var baseDir = Path.IsPathRooted(opts.BasePath)
            ? opts.BasePath
            : Path.Combine(AppContext.BaseDirectory, opts.BasePath);

        _filePath = Path.Combine(baseDir, "rules.md");
        _stamp = StampOf(_filePath);
        _rules = ExtractRules(ReadLines());

        _logger.LogInformation("Rules store initialised — {Count} rule(s) loaded from {Path}",
            _rules.Count, _filePath);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Rules => RefreshIfChanged();

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Read-through rather than serving the cache: this is the surface behind
            // list_rules, and answering it from a startup snapshot would hide any rule
            // added to the file since. Stamped so the next Rules read does not re-parse
            // content this call has already loaded.
            _stamp = StampOf(_filePath);
            _rules = ExtractRules(ReadLines());
            return _rules;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task AddAsync(string rule)
    {
        await _lock.WaitAsync();
        try
        {
            var lines = ReadLines();
            var bullets = FindBullets(lines);

            if (bullets.Any(b => b.Text.Equals(rule, StringComparison.OrdinalIgnoreCase)))
            {
                _rules = bullets.Select(b => b.Text).ToList();
                _logger.LogDebug("AddRule: rule already exists, skipping — {Rule}", rule);
                return;
            }

            if (bullets.Count > 0)
            {
                // Straight after the last existing rule, so the block stays contiguous and
                // anything written below it (notes, other sections) stays below it.
                lines.Insert(bullets[^1].Index + 1, $"- {rule}");
            }
            else if (lines.Count == 0)
            {
                lines.AddRange(["# Active Rules", string.Empty, $"- {rule}"]);
            }
            else
            {
                lines.Add($"- {rule}");
            }

            await PersistAsync(lines);
            _logger.LogInformation("Added rule: {Rule}", rule);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string rule)
    {
        await _lock.WaitAsync();
        try
        {
            var lines = ReadLines();
            var matches = FindBullets(lines)
                .Where(b => b.Text.Equals(rule, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                _rules = ExtractRules(lines);
                _logger.LogDebug("RemoveRule: rule not found — {Rule}", rule);
                return;
            }

            // Descending so earlier indices stay valid as lines are removed.
            foreach (var match in matches.OrderByDescending(m => m.Index))
                lines.RemoveAt(match.Index);

            await PersistAsync(lines);
            _logger.LogInformation("Removed rule: {Rule}", rule);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ContentEditResult> EditAsync(string oldText, string newText, bool replaceAll = false)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        // An empty or unchanged oldText is rejected before any content is inspected, and the
        // wording comes from the shared primitive rather than being restated here.
        if (oldText.Length == 0 || string.Equals(oldText, newText, StringComparison.Ordinal))
            return ContentEditResult.Failed(TextEdit.Apply(string.Empty, oldText, newText, replaceAll).Error!);

        await _lock.WaitAsync();
        try
        {
            var lines = ReadLines();
            var matches = FindBullets(lines)
                .Select(b => (b.Index, b.Prefix, b.Text, Count: TextEdit.CountOccurrences(b.Text, oldText)))
                .Where(b => b.Count > 0)
                .ToList();

            var total = matches.Sum(m => m.Count);

            if (total == 0)
            {
                _rules = ExtractRules(lines);
                return ContentEditResult.Failed(
                    "oldText was not found in any active rule. It must match the rule text exactly — " +
                    "list the rules and copy it verbatim.");
            }

            if (total > 1 && !replaceAll)
            {
                _rules = ExtractRules(lines);
                return ContentEditResult.Failed(
                    $"oldText occurs {total} times across the active rules — the edit target is ambiguous. " +
                    "Either include more of the rule so the match is unique, or set replaceAll to change " +
                    "every occurrence.");
            }

            var oldLength = 0;
            var newLength = 0;
            var replacements = 0;

            foreach (var match in matches)
            {
                var edit = TextEdit.Apply(match.Text, oldText, newText, replaceAll);
                if (!edit.IsSuccess)
                    return ContentEditResult.Failed(edit.Error!);

                lines[match.Index] = match.Prefix + edit.Content;
                oldLength += match.Text.Length;
                newLength += edit.Content!.Length;
                replacements += edit.ReplacementCount;
            }

            await PersistAsync(lines);

            _logger.LogInformation(
                "Edited rules — {Replacements} replacement(s) across {Rules} rule(s)",
                replacements, matches.Count);

            return ContentEditResult.Applied(replacements, oldLength, newLength);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>A bullet line: where it is, the marker and indentation, and the rule text.</summary>
    private readonly record struct Bullet(int Index, string Prefix, string Text);

    /// <summary>
    /// Serves the cached rules, re-parsing first when <c>rules.md</c> has changed on disk since
    /// the last parse. Any failure to read leaves the previous rules in place: a rule already
    /// being followed is not dropped because of a transient filesystem error, and dropping one
    /// silently is the direction that actually causes harm.
    /// </summary>
    /// <remarks>
    /// Change detection is timestamp plus length, which shares one limitation with every other
    /// stat-based check in the host: two writes inside the same filesystem timestamp tick that
    /// leave the length identical are indistinguishable. For an operator-pushed file of a few
    /// bullets that is not a case worth a hash; the mutation paths above re-read unconditionally
    /// and are unaffected.
    /// </remarks>
    private IReadOnlyList<string> RefreshIfChanged()
    {
        var stamp = StampOf(_filePath);

        // Stat failed outright — distinct from the file being absent, which has its own stamp.
        // Keep serving what we have rather than reporting no rules on a transient error.
        if (stamp is null)
            return _rules;

        if (stamp == _stamp)
            return _rules;

        lock (_refreshGate)
        {
            // Re-checked inside the gate: a concurrent reader may already have reloaded this
            // same change, and parsing it twice would be pure waste.
            if (stamp == _stamp)
                return _rules;

            try
            {
                var rules = ExtractRules(ReadLines());
                var before = _rules.Count;
                _rules = rules;
                _stamp = stamp;

                _logger.LogInformation(
                    "rules.md changed on disk — reloaded {Count} rule(s) (was {Before})",
                    rules.Count, before);

                return rules;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not stamping: the next read retries rather than treating the
                // failed parse as the current state of the file.
                _logger.LogWarning(ex,
                    "Could not reload rules.md; continuing with the {Count} rule(s) already loaded",
                    _rules.Count);
                return _rules;
            }
        }
    }

    /// <summary>
    /// The stamp of a file that is not there. A distinct value rather than <c>null</c> so that
    /// deleting <c>rules.md</c> registers as a change and clears the rules, while a stat that
    /// throws stays unknown and changes nothing.
    /// </summary>
    private static readonly (DateTime LastWriteUtc, long Length) Missing = (DateTime.MinValue, -1);

    /// <summary>
    /// Timestamp and length of <paramref name="path"/>, <see cref="Missing"/> when it does not
    /// exist, or <c>null</c> when it cannot be stat'd at all.
    /// </summary>
    private static (DateTime LastWriteUtc, long Length)? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<string> ReadLines() =>
        File.Exists(_filePath) ? [.. File.ReadAllLines(_filePath)] : [];

    /// <summary>
    /// Returns the bullet lines in <paramref name="lines"/>, in document order. A bullet is a
    /// line beginning (after optional indentation) with <c>-</c> or <c>*</c> and a space;
    /// everything else — headings, prose, blanks — is not a rule and is left alone.
    /// </summary>
    private static List<Bullet> FindBullets(List<string> lines)
    {
        var bullets = new List<Bullet>();

        for (var i = 0; i < lines.Count; i++)
        {
            var match = BulletPattern().Match(lines[i]);
            if (!match.Success)
                continue;

            var prefix = match.Value;
            var text = lines[i][prefix.Length..].TrimEnd();
            if (text.Length > 0)
                bullets.Add(new Bullet(i, prefix, text));
        }

        return bullets;
    }

    private static IReadOnlyList<string> ExtractRules(List<string> lines) =>
        FindBullets(lines).Select(b => b.Text).ToList();

    private async Task PersistAsync(List<string> lines)
    {
        await AtomicFile.WriteAllTextAsync(
            _filePath,
            lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines) + Environment.NewLine);

        // Stamp after the rename, so this store's own write is not mistaken for an external
        // edit and re-parsed on the next prompt.
        _stamp = StampOf(_filePath);
        _rules = ExtractRules(lines);
    }

    [GeneratedRegex(@"^\s*[-*]\s+")]
    private static partial Regex BulletPattern();
}
