using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Applies a <see cref="RepairTarget.SkillBody"/> change by mutating a named
/// skill's body via append, replaceSection, or deleteSection ops. The original
/// body is captured at apply time so verify failures can revert the change
/// before the next attempt.
/// </summary>
internal sealed class SkillBodyApplier : IRepairTargetApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ISkillStore _skillStore;
    private readonly ILogger<SkillBodyApplier> _logger;

    public SkillBodyApplier(ISkillStore skillStore, ILogger<SkillBodyApplier> logger)
    {
        _skillStore = skillStore ?? throw new ArgumentNullException(nameof(skillStore));
        _logger = logger;
    }

    public RepairTarget Target => RepairTarget.SkillBody;

    public async Task<RepairApplyOutcome> ApplyAsync(RepairTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var change = ticket.Change.Deserialize<SkillBodyChange>(JsonOptions)
            ?? throw new ArgumentException("SkillBody change is empty.", nameof(ticket));

        var skill = change.ResolvedSkill;
        if (string.IsNullOrWhiteSpace(skill))
            throw new ArgumentException(
                "SkillBody change missing 'skill' (also accepted: 'skillName', 'name').", nameof(ticket));
        if (change.Ops is null || change.Ops.Count == 0)
            throw new ArgumentException("SkillBody change has no ops.", nameof(ticket));

        var existing = await _skillStore.GetAsync(skill)
            ?? throw new InvalidOperationException($"Skill '{skill}' not found.");

        var preBody = existing.Content ?? string.Empty;
        var preHash = HashOf(preBody);
        var preUpdatedAt = existing.UpdatedAt;

        var newBody = preBody;
        foreach (var op in change.Ops)
        {
            newBody = ApplyOp(newBody, op);
        }

        var postHash = HashOf(newBody);

        var updated = existing with
        {
            Content = newBody,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _skillStore.SaveAsync(updated);

        var diff = JsonSerializer.SerializeToElement(new
        {
            skill = skill,
            preHash,
            postHash,
            ops = change.Ops,
        }, JsonOptions);

        _logger.LogInformation(
            "SkillBodyApplier: applied {OpCount} op(s) to skill {Skill} (pre={PreHash} post={PostHash})",
            change.Ops.Count, skill, preHash, postHash);

        Func<CancellationToken, Task> revert = async ct =>
        {
            var current = await _skillStore.GetAsync(skill);
            if (current is null)
            {
                _logger.LogWarning("SkillBodyApplier revert: skill {Skill} no longer exists", skill);
                return;
            }

            // If the user (or another writer) edited the skill between apply and revert,
            // skip revert — clobbering their edit would surprise them more than leaving
            // our verify-failed change in place. The escalation summary will surface the
            // problem.
            if (current.UpdatedAt != updated.UpdatedAt)
            {
                _logger.LogWarning(
                    "SkillBodyApplier revert: skill {Skill} updated by another writer (expected {Expected}, found {Actual}); skipping revert",
                    skill, updated.UpdatedAt, current.UpdatedAt);
                return;
            }

            var reverted = current with
            {
                Content = preBody,
                UpdatedAt = preUpdatedAt ?? DateTimeOffset.UtcNow,
            };
            await _skillStore.SaveAsync(reverted);
            _logger.LogInformation("SkillBodyApplier reverted skill {Skill} to pre-apply body", skill);
        };

        return new RepairApplyOutcome(diff, revert);
    }

    internal static string ApplyOp(string body, SkillBodyOp op)
    {
        return op.Op?.ToLowerInvariant() switch
        {
            "append" => Append(body, op.Text ?? string.Empty),
            "replacesection" => ReplaceSection(body, RequireHeader(op), op.Text ?? string.Empty),
            "deletesection" => DeleteSection(body, RequireHeader(op)),
            _ => throw new ArgumentException($"Unknown SkillBody op: '{op.Op}'.", nameof(op)),
        };

        static string RequireHeader(SkillBodyOp op) =>
            string.IsNullOrWhiteSpace(op.Header)
                ? throw new ArgumentException($"Op '{op.Op}' requires 'header'.")
                : op.Header.TrimEnd();
    }

    private static string Append(string body, string text)
    {
        if (string.IsNullOrEmpty(text)) return body;
        if (body.Length == 0) return text.TrimEnd() + "\n";
        return body.TrimEnd() + "\n\n" + text.TrimEnd() + "\n";
    }

    private static string ReplaceSection(string body, string header, string newText)
    {
        var (start, end) = FindSection(body, header);
        if (start < 0)
            throw new InvalidOperationException($"Section '{header}' not found.");

        // Rebuild: prefix + header + blank line + new text + suffix.
        var prefix = body[..start].TrimEnd();
        var suffix = end < body.Length ? body[end..].TrimStart('\r', '\n') : string.Empty;
        var sb = new StringBuilder();
        if (prefix.Length > 0)
        {
            sb.Append(prefix);
            sb.Append("\n\n");
        }
        sb.Append(header.TrimEnd());
        sb.Append("\n\n");
        sb.Append(newText.TrimEnd());
        sb.Append('\n');
        if (suffix.Length > 0)
        {
            sb.Append('\n');
            sb.Append(suffix.TrimEnd());
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string DeleteSection(string body, string header)
    {
        var (start, end) = FindSection(body, header);
        if (start < 0)
            throw new InvalidOperationException($"Section '{header}' not found.");

        var prefix = body[..start].TrimEnd();
        var suffix = end < body.Length ? body[end..].TrimStart('\r', '\n') : string.Empty;
        if (prefix.Length == 0) return suffix.TrimEnd() + (suffix.Length > 0 ? "\n" : string.Empty);
        if (suffix.Length == 0) return prefix + "\n";
        return prefix + "\n\n" + suffix.TrimEnd() + "\n";
    }

    /// <summary>
    /// Returns (start, end) byte offsets of the section identified by <paramref name="header"/>.
    /// Section starts at the beginning of the header line and runs until the next H1/H2/H3
    /// header line or EOF. Returns (-1, -1) when not found.
    /// </summary>
    internal static (int Start, int End) FindSection(string body, string header)
    {
        var lines = body.Split('\n');
        var lineStartOffset = 0;

        var sectionStart = -1;
        var sectionEnd = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var stripped = rawLine.TrimEnd('\r');

            if (sectionStart < 0)
            {
                if (string.Equals(stripped.TrimEnd(), header.TrimEnd(), StringComparison.Ordinal))
                {
                    sectionStart = lineStartOffset;
                }
            }
            else
            {
                // Already inside the section; stop at the next H1/H2/H3 header.
                if (IsHeaderLine(stripped))
                {
                    sectionEnd = lineStartOffset;
                    break;
                }
            }

            lineStartOffset += rawLine.Length + 1; // +1 for the '\n' the split removed
        }

        if (sectionStart < 0) return (-1, -1);
        if (sectionEnd < 0) sectionEnd = body.Length;
        return (sectionStart, sectionEnd);
    }

    private static bool IsHeaderLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("# ")) return true;
        if (trimmed.StartsWith("## ")) return true;
        if (trimmed.StartsWith("### ")) return true;
        return false;
    }

    private static string HashOf(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    internal sealed class SkillBodyChange
    {
        public string? Skill { get; set; }

        /// <summary>Accepted spelling of <see cref="Skill"/>.</summary>
        public string? SkillName { get; set; }

        /// <summary>Accepted spelling of <see cref="Skill"/>.</summary>
        public string? Name { get; set; }

        public List<SkillBodyOp>? Ops { get; set; }

        /// <summary>
        /// The skill this change targets, under whichever of the accepted spellings the ticket
        /// used.
        /// </summary>
        /// <remarks>
        /// The creation directive documents the <c>ops</c> array for this target but never named
        /// the identifier field, while it spells the fields out for every other target. Models
        /// filled the gap with the obvious synonyms, and every such ticket then failed validation
        /// here — on one agent, the same ticket failed 117 times over two months. Accepting the
        /// synonyms costs nothing: the change object carries only a skill and its ops, so there is
        /// no other field these names could plausibly mean.
        /// </remarks>
        public string? ResolvedSkill =>
            new[] { Skill, SkillName, Name }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    internal sealed class SkillBodyOp
    {
        public string? Op { get; set; }
        public string? Header { get; set; }
        public string? Text { get; set; }
    }
}
