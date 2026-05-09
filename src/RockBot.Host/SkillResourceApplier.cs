using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Applies a <see cref="RepairTarget.SkillResource"/> change against a named skill's
/// resource folder. Three ops are supported:
/// <list type="bullet">
/// <item><c>attach</c> — attach a new resource (always provisional, since self-repair
/// attaches are hypotheses awaiting validation by the dream-cycle pass).</item>
/// <item><c>delete</c> — remove a resource by filename.</item>
/// <item><c>demote-provisional</c> — flip a manifest entry's <c>Provisional=true</c>
/// (the validation pass normally flips false; this op is the inverse, used when a
/// resource needs to be re-validated after suspicion).</item>
/// </list>
/// Both <c>attach</c> and <c>delete</c> ship a revert callback so a verify failure can
/// roll the change back without requiring a second LLM round-trip.
/// </summary>
internal sealed class SkillResourceApplier : IRepairTargetApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ISkillStore _skillStore;
    private readonly ILogger<SkillResourceApplier> _logger;

    public SkillResourceApplier(ISkillStore skillStore, ILogger<SkillResourceApplier> logger)
    {
        _skillStore = skillStore ?? throw new ArgumentNullException(nameof(skillStore));
        _logger = logger;
    }

    public RepairTarget Target => RepairTarget.SkillResource;

    public async Task<RepairApplyOutcome> ApplyAsync(RepairTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var change = ticket.Change.Deserialize<SkillResourceChange>(JsonOptions)
            ?? throw new ArgumentException("SkillResource change is empty.", nameof(ticket));

        if (string.IsNullOrWhiteSpace(change.Skill))
            throw new ArgumentException("SkillResource change missing 'skill'.", nameof(ticket));
        if (string.IsNullOrWhiteSpace(change.Filename))
            throw new ArgumentException("SkillResource change missing 'filename'.", nameof(ticket));

        var op = (change.Op ?? string.Empty).ToLowerInvariant();
        return op switch
        {
            "attach" => await ApplyAttachAsync(change, cancellationToken),
            "delete" => await ApplyDeleteAsync(change, cancellationToken),
            "demote-provisional" => await ApplyDemoteAsync(change, cancellationToken),
            _ => throw new ArgumentException(
                $"Unknown SkillResource op: '{change.Op}'. Expected attach, delete, or demote-provisional.",
                nameof(ticket)),
        };
    }

    private async Task<RepairApplyOutcome> ApplyAttachAsync(SkillResourceChange change, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(change.Content))
            throw new ArgumentException("SkillResource attach requires 'content'.", nameof(change));

        // Capture pre-state so revert can restore the prior body (or remove if absent).
        var prior = await _skillStore.GetAsync(change.Skill!);
        if (prior is null)
            throw new InvalidOperationException($"Skill '{change.Skill}' not found.");

        var priorEntry = prior.Manifest?
            .FirstOrDefault(r => string.Equals(r.Filename, change.Filename, StringComparison.OrdinalIgnoreCase));
        var priorBody = priorEntry is null
            ? null
            : await _skillStore.GetResourceAsync(change.Skill!, change.Filename!);

        var resourceType = change.Type ?? SkillResourceType.Wisp;
        var description = change.Description ?? change.Filename!;

        var input = new SkillResourceInput(
            change.Filename!, resourceType, description, change.Content!,
            Provisional: true,
            VerifyHint: change.VerifyHint);
        var attached = await _skillStore.AttachResourceAsync(change.Skill!, input);
        if (!attached)
            throw new InvalidOperationException($"Skill '{change.Skill}' not found.");

        var diff = JsonSerializer.SerializeToElement(new
        {
            op = "attach",
            skill = change.Skill,
            filename = change.Filename,
            type = resourceType.ToString(),
            replacedExisting = priorEntry is not null,
        }, JsonOptions);

        _logger.LogInformation(
            "SkillResourceApplier: attached '{File}' to skill '{Skill}' (provisional, replacedExisting={Replaced})",
            change.Filename, change.Skill, priorEntry is not null);

        // Revert: if there was no prior entry, remove the one we just added; if there
        // was, restore the prior body and entry verbatim.
        Func<CancellationToken, Task> revert = async revertCt =>
        {
            if (priorEntry is null)
            {
                await _skillStore.RemoveResourceAsync(change.Skill!, change.Filename!);
                _logger.LogInformation(
                    "SkillResourceApplier reverted attach: removed '{File}' from skill '{Skill}'",
                    change.Filename, change.Skill);
            }
            else
            {
                var restoreInput = new SkillResourceInput(
                    priorEntry.Filename, priorEntry.Type, priorEntry.Description, priorBody ?? string.Empty,
                    Provisional: priorEntry.Provisional,
                    VerifyHint: priorEntry.VerifyHint);
                await _skillStore.AttachResourceAsync(change.Skill!, restoreInput, priorEntry);
                _logger.LogInformation(
                    "SkillResourceApplier reverted attach: restored prior '{File}' on skill '{Skill}'",
                    change.Filename, change.Skill);
            }
        };

        return new RepairApplyOutcome(diff, revert);
    }

    private async Task<RepairApplyOutcome> ApplyDeleteAsync(SkillResourceChange change, CancellationToken ct)
    {
        var prior = await _skillStore.GetAsync(change.Skill!);
        if (prior is null)
            throw new InvalidOperationException($"Skill '{change.Skill}' not found.");
        var priorEntry = prior.Manifest?
            .FirstOrDefault(r => string.Equals(r.Filename, change.Filename, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Resource '{change.Filename}' not found on skill '{change.Skill}'.");

        var priorBody = await _skillStore.GetResourceAsync(change.Skill!, change.Filename!)
            ?? throw new InvalidOperationException(
                $"Resource '{change.Filename}' on skill '{change.Skill}' has no body to capture for revert.");

        var removed = await _skillStore.RemoveResourceAsync(change.Skill!, change.Filename!);
        if (!removed)
            throw new InvalidOperationException(
                $"Failed to remove resource '{change.Filename}' from skill '{change.Skill}'.");

        var diff = JsonSerializer.SerializeToElement(new
        {
            op = "delete",
            skill = change.Skill,
            filename = change.Filename,
        }, JsonOptions);

        _logger.LogInformation(
            "SkillResourceApplier: deleted '{File}' from skill '{Skill}'",
            change.Filename, change.Skill);

        Func<CancellationToken, Task> revert = async revertCt =>
        {
            var restoreInput = new SkillResourceInput(
                priorEntry.Filename, priorEntry.Type, priorEntry.Description, priorBody,
                Provisional: priorEntry.Provisional,
                VerifyHint: priorEntry.VerifyHint);
            await _skillStore.AttachResourceAsync(change.Skill!, restoreInput, priorEntry);
            _logger.LogInformation(
                "SkillResourceApplier reverted delete: restored '{File}' on skill '{Skill}'",
                change.Filename, change.Skill);
        };

        return new RepairApplyOutcome(diff, revert);
    }

    private async Task<RepairApplyOutcome> ApplyDemoteAsync(SkillResourceChange change, CancellationToken ct)
    {
        var prior = await _skillStore.GetAsync(change.Skill!);
        if (prior is null)
            throw new InvalidOperationException($"Skill '{change.Skill}' not found.");
        var priorEntry = prior.Manifest?
            .FirstOrDefault(r => string.Equals(r.Filename, change.Filename, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Resource '{change.Filename}' not found on skill '{change.Skill}'.");

        if (priorEntry.Provisional)
        {
            // Already provisional — no-op, no revert needed.
            var diffNoop = JsonSerializer.SerializeToElement(new
            {
                op = "demote-provisional",
                skill = change.Skill,
                filename = change.Filename,
                changed = false,
            }, JsonOptions);
            return new RepairApplyOutcome(diffNoop, Revert: null);
        }

        var demoted = priorEntry with { Provisional = true };
        var ok = await _skillStore.UpdateResourceMetadataAsync(change.Skill!, demoted);
        if (!ok)
            throw new InvalidOperationException(
                $"Failed to demote resource '{change.Filename}' on skill '{change.Skill}'.");

        var diff = JsonSerializer.SerializeToElement(new
        {
            op = "demote-provisional",
            skill = change.Skill,
            filename = change.Filename,
            changed = true,
        }, JsonOptions);

        Func<CancellationToken, Task> revert = async revertCt =>
        {
            await _skillStore.UpdateResourceMetadataAsync(change.Skill!, priorEntry);
            _logger.LogInformation(
                "SkillResourceApplier reverted demote: restored Provisional=false on '{File}' for skill '{Skill}'",
                change.Filename, change.Skill);
        };

        return new RepairApplyOutcome(diff, revert);
    }

    internal sealed class SkillResourceChange
    {
        public string? Skill { get; set; }
        public string? Filename { get; set; }
        public string? Op { get; set; }
        public string? Content { get; set; }
        public SkillResourceType? Type { get; set; }
        public string? Description { get; set; }
        public string? VerifyHint { get; set; }
    }
}
