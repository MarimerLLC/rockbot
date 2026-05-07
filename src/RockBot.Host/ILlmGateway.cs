namespace RockBot.Host;

/// <summary>
/// Global per-tier concurrency layer for LLM calls. All <see cref="ILlmClient"/>
/// invocations flow through an implementation of this gateway so that parallel
/// callers cannot overwhelm a tier and so that cancellation reliably drains
/// pending work. See <c>design/llm-gateway.md</c>.
/// </summary>
internal interface ILlmGateway
{
    /// <summary>
    /// Acquires a slot on the per-tier concurrency semaphore, then invokes
    /// <paramref name="operation"/>. If <paramref name="cancellationToken"/>
    /// fires while waiting for a slot, the wait aborts with
    /// <see cref="OperationCanceledException"/> before the operation runs.
    /// </summary>
    /// <remarks>
    /// The same <paramref name="cancellationToken"/> is passed to
    /// <paramref name="operation"/>. Implementations must propagate cancellation
    /// end-to-end; any path that drops the token re-introduces the rate-limit and
    /// preemption hazards the gateway exists to prevent.
    /// </remarks>
    Task<T> ExecuteAsync<T>(
        ModelTier tier,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}
