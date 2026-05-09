using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Applies a <see cref="RepairTarget.PromptBuilderHint"/> change by appending or
/// replacing a delimited hint section in <c>/data/agent/prompt-hints/{category}.md</c>.
/// The default system prompt builder reads these files when assembling the
/// system prompt for sessions in the matching category. Idempotent by <c>hintId</c>:
/// re-applying the same id replaces the existing section in place.
/// </summary>
internal sealed class PromptBuilderHintApplier : IRepairTargetApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<PromptBuilderHintApplier> _logger;
    private readonly string _basePath;

    public PromptBuilderHintApplier(
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<PromptBuilderHintApplier> logger)
    {
        _logger = logger;
        _basePath = ResolvePath("prompt-hints", profileOptions.Value.BasePath);
    }

    public RepairTarget Target => RepairTarget.PromptBuilderHint;

    public async Task<RepairApplyOutcome> ApplyAsync(RepairTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var change = ticket.Change.Deserialize<PromptBuilderHintChange>(JsonOptions)
            ?? throw new ArgumentException("PromptBuilderHint change is empty.", nameof(ticket));

        if (string.IsNullOrWhiteSpace(change.Category))
            throw new ArgumentException("PromptBuilderHint change missing 'category'.", nameof(ticket));
        if (string.IsNullOrWhiteSpace(change.HintId))
            throw new ArgumentException("PromptBuilderHint change missing 'hintId'.", nameof(ticket));
        if (string.IsNullOrWhiteSpace(change.Text))
            throw new ArgumentException("PromptBuilderHint change missing 'text'.", nameof(ticket));

        if (!IsSafeFileName(change.Category!) || !IsSafeFileName(change.HintId!))
            throw new ArgumentException("Category and hintId must be safe file-name segments.", nameof(ticket));

        Directory.CreateDirectory(_basePath);
        var filePath = Path.Combine(_basePath, change.Category + ".md");

        var existing = File.Exists(filePath)
            ? await File.ReadAllTextAsync(filePath, cancellationToken)
            : string.Empty;

        var newSection = BuildSection(change.HintId!, change.Text!);

        var (replaced, updated) = ReplaceSection(existing, change.HintId!, newSection);
        if (!replaced)
        {
            updated = AppendSection(existing, newSection);
        }

        var tmp = filePath + ".tmp";
        await File.WriteAllTextAsync(tmp, updated, cancellationToken);
        File.Move(tmp, filePath, overwrite: true);

        var diff = JsonSerializer.SerializeToElement(new
        {
            category = change.Category,
            hintId = change.HintId,
            action = replaced ? "replaced" : "appended",
            length = updated.Length,
        }, JsonOptions);

        _logger.LogInformation(
            "PromptBuilderHintApplier: {Action} hint {HintId} in {Category}",
            replaced ? "replaced" : "appended", change.HintId, change.Category);

        return new RepairApplyOutcome(diff, Revert: null);
    }

    internal static string BuildSection(string hintId, string text)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- hint:").Append(hintId).Append(" -->\n");
        sb.Append(text.TrimEnd()).Append('\n');
        sb.Append("<!-- /hint:").Append(hintId).Append(" -->\n");
        return sb.ToString();
    }

    internal static (bool Replaced, string Updated) ReplaceSection(string existing, string hintId, string newSection)
    {
        // Match the entire section including the open/close markers.
        var pattern = "<!--\\s*hint:" + Regex.Escape(hintId) + "\\s*-->" +
                      "[\\s\\S]*?" +
                      "<!--\\s*/hint:" + Regex.Escape(hintId) + "\\s*-->\\s*\\n?";
        var rx = new Regex(pattern, RegexOptions.IgnoreCase);
        if (!rx.IsMatch(existing))
            return (false, existing);

        var replaced = rx.Replace(existing, newSection.TrimEnd() + "\n", 1);
        return (true, replaced);
    }

    internal static string AppendSection(string existing, string newSection)
    {
        if (string.IsNullOrEmpty(existing))
            return newSection;

        var trimmed = existing.TrimEnd();
        return trimmed + "\n\n" + newSection;
    }

    private static bool IsSafeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Contains('/') || s.Contains('\\') || s.Contains("..")) return false;
        foreach (var c in Path.GetInvalidFileNameChars())
            if (s.Contains(c)) return false;
        return true;
    }

    private static string ResolvePath(string path, string profileBasePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, path);
    }

    internal sealed class PromptBuilderHintChange
    {
        public string? Category { get; set; }
        public string? HintId { get; set; }
        public string? Text { get; set; }
    }
}
