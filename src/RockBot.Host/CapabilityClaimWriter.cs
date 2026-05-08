using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Default <see cref="ICapabilityClaimWriter"/>. Validates the claim shape, builds the
/// conventional <c>claim/capability/{server}/{tool}</c> category, and persists the entry
/// via <see cref="ILongTermMemory"/> with the verify shape attached.
/// </summary>
internal sealed class CapabilityClaimWriter : ICapabilityClaimWriter
{
    private readonly ILongTermMemory _memory;
    private readonly IMemoryContradictionDetector? _contradictionDetector;
    private readonly ILogger<CapabilityClaimWriter>? _logger;

    public CapabilityClaimWriter(ILongTermMemory memory)
        : this(memory, contradictionDetector: null, logger: null) { }

    public CapabilityClaimWriter(
        ILongTermMemory memory,
        IMemoryContradictionDetector? contradictionDetector,
        ILogger<CapabilityClaimWriter>? logger)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _contradictionDetector = contradictionDetector;
        _logger = logger;
    }

    public async Task SaveCapabilityClaimAsync(CapabilityClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        Validate(claim);

        var entry = new MemoryEntry(
            Id: BuildId(claim),
            Content: claim.Statement,
            Category: CapabilityClaimCategories.For(claim.Server, claim.Tool),
            Tags: BuildTags(claim),
            CreatedAt: claim.CreatedAt,
            Metadata: BuildMetadata(claim))
        {
            Verify = claim.Verify
        };

        if (_contradictionDetector is not null)
        {
            var resolution = await _contradictionDetector.ResolveAsync(entry, cancellationToken);
            if (resolution.IncomingSupersededBy is not null)
            {
                // An existing user-correction wins — the new claim lands on disk already marked
                // as superseded so it is excluded from search/recall but preserved for audit.
                entry = entry with { SupersededBy = resolution.IncomingSupersededBy };
                _logger?.LogInformation(
                    "CapabilityClaimWriter: incoming claim {Id} marked superseded by user-correction {ExistingId}",
                    entry.Id, resolution.IncomingSupersededBy);
            }
            else if (resolution.ExistingIdsToSupersede.Count > 0)
            {
                await ApplySupersessionAsync(resolution.ExistingIdsToSupersede, entry.Id, cancellationToken);
            }
        }

        await _memory.SaveAsync(entry, cancellationToken);
    }

    private async Task ApplySupersessionAsync(IReadOnlyList<string> ids, string winnerId, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            var existing = await _memory.GetAsync(id, ct);
            if (existing is null) continue;
            if (existing.SupersededBy is not null) continue;

            await _memory.SaveAsync(
                existing with { SupersededBy = winnerId, UpdatedAt = DateTimeOffset.UtcNow },
                ct);

            _logger?.LogInformation(
                "CapabilityClaimWriter: marked {ExistingId} superseded by {WinnerId} ({Category})",
                id, winnerId, existing.Category ?? "(none)");
        }
    }

    private static void Validate(CapabilityClaim claim)
    {
        if (string.IsNullOrWhiteSpace(claim.Server))
            throw new ArgumentException("Capability claim requires a non-empty Server.", nameof(claim));
        if (string.IsNullOrWhiteSpace(claim.Tool))
            throw new ArgumentException("Capability claim requires a non-empty Tool.", nameof(claim));
        if (string.IsNullOrWhiteSpace(claim.Statement))
            throw new ArgumentException("Capability claim requires a non-empty Statement.", nameof(claim));
        if (claim.Verify is null)
            throw new ArgumentException(
                "Capability claim requires a VerifyShape — claims without a falsifiable predicate are rejected.",
                nameof(claim));
        if (string.IsNullOrWhiteSpace(claim.Verify.Server))
            throw new ArgumentException("VerifyShape requires a non-empty Server.", nameof(claim));
        if (string.IsNullOrWhiteSpace(claim.Verify.Tool))
            throw new ArgumentException("VerifyShape requires a non-empty Tool.", nameof(claim));
        if (claim.Verify.Expect.Kind == VerifyExpectationKind.FailureWithMessage
            && string.IsNullOrEmpty(claim.Verify.Expect.FailurePattern))
        {
            throw new ArgumentException(
                "VerifyExpectationKind.FailureWithMessage requires a non-empty FailurePattern.",
                nameof(claim));
        }
    }

    private static string BuildId(CapabilityClaim claim)
    {
        // Hash over (server, tool, statement) so re-saving an identical claim overwrites
        // rather than accumulating duplicates; different statements for the same (server, tool)
        // coexist as separate entries.
        var text = $"{claim.Server}|{claim.Tool}|{claim.Statement}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return $"claim-{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
    }

    private static IReadOnlyList<string> BuildTags(CapabilityClaim claim) =>
    [
        "capability-claim",
        $"server:{claim.Server}",
        $"tool:{claim.Tool}"
    ];

    private static IReadOnlyDictionary<string, string> BuildMetadata(CapabilityClaim claim)
    {
        var metadata = new Dictionary<string, string>
        {
            ["kind"] = "capability-claim",
            ["server"] = claim.Server,
            ["tool"] = claim.Tool
        };

        if (claim.Evidence is { Count: > 0 })
        {
            metadata["evidenceCount"] = claim.Evidence.Count.ToString(CultureInfo.InvariantCulture);

            // Compact join — clip per-item and total to keep entries searchable but bounded.
            var joined = string.Join(" | ", claim.Evidence.Select(e =>
                e.Length > 256 ? e[..256] + "…" : e));
            metadata["evidence"] = joined.Length > 4096 ? joined[..4096] + "…" : joined;
        }

        return metadata;
    }
}
