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

    public FileRulesStore(IOptions<AgentProfileOptions> options, ILogger<FileRulesStore> logger)
    {
        _logger = logger;

        var opts = options.Value;
        var baseDir = Path.IsPathRooted(opts.BasePath)
            ? opts.BasePath
            : Path.Combine(AppContext.BaseDirectory, opts.BasePath);

        _filePath = Path.Combine(baseDir, "rules.md");
        _rules = ExtractRules(ReadLines());

        _logger.LogInformation("Rules store initialised — {Count} rule(s) loaded from {Path}",
            _rules.Count, _filePath);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Rules => _rules;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Read-through rather than serving the cache: this is the surface behind
            // list_rules, and answering it from a startup snapshot would hide any rule
            // added to the file since.
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

        _rules = ExtractRules(lines);
    }

    [GeneratedRegex(@"^\s*[-*]\s+")]
    private static partial Regex BulletPattern();
}
