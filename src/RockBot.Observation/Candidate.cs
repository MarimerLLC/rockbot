namespace RockBot.Observation;

/// <summary>
/// A candidate observation: extracted from one or more conversations but not yet
/// reinforced enough times to graduate into a <see cref="Theory"/>. Each candidate
/// represents one logical claim; multiple textual variants from different
/// conversations are merged into the same candidate via vector clustering.
/// </summary>
public sealed class Candidate
{
    /// <summary>Stable ID assigned at creation time.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Canonical text of the observation. Updated when the cluster picks up new
    /// references whose phrasing better captures the underlying claim; the
    /// canonical-form rewrite is performed by a Low-tier LLM call from the
    /// pipeline's merge step, never by direct user input.
    /// </summary>
    public required string Text { get; set; }

    /// <summary>
    /// Vector cluster the candidate belongs to. New observations join the
    /// candidate when their embedding falls within the configured similarity
    /// threshold of this cluster.
    /// </summary>
    public required string ClusterId { get; init; }

    /// <summary>
    /// Number of distinct conversations contributing to this candidate. This is
    /// the metric the promotion threshold compares against — derivable from
    /// <see cref="References"/> but cached here for fast reads in the markdown
    /// template and during evaluation.
    /// </summary>
    public int Count { get; set; }

    /// <summary>When this candidate was first created.</summary>
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>
    /// Most recent reference observation timestamp. Used by the aging pass: a
    /// candidate with no new references in the configured candidate-aging window
    /// is dropped.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// Evidence backing this candidate. Each reference pins the observation to
    /// a specific conversation and turn with a verbatim quote. References from
    /// the same conversation collapse to a single contributing conversation
    /// for promotion-threshold counting.
    /// </summary>
    public List<ObservationReference> References { get; init; } = [];
}
