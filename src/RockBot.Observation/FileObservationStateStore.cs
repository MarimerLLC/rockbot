using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Observation;

/// <summary>
/// Filesystem-backed <see cref="IObservationStateStore"/>. Writes go to
/// <c>&lt;StateFilePath&gt;.tmp</c> and are renamed into place; a crash mid-write
/// leaves the canonical file intact. Schema-version mismatches are surfaced as
/// exceptions rather than silently coerced.
/// </summary>
internal sealed class FileObservationStateStore(ILogger<FileObservationStateStore> logger)
    : IObservationStateStore
{
    public async Task<ObservationState> LoadAsync(
        ObservationTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!File.Exists(target.StateFilePath))
        {
            logger.LogInformation(
                "Observation: no state file for target {Target} at {Path}; starting fresh",
                target.Name, target.StateFilePath);
            return new ObservationState();
        }

        await using var stream = new FileStream(
            target.StateFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var state = await JsonSerializer.DeserializeAsync<ObservationState>(
            stream, ObservationStateJsonOptions.Instance, cancellationToken).ConfigureAwait(false);

        if (state is null)
            throw new InvalidDataException(
                $"Observation state at {target.StateFilePath} deserialised to null.");

        if (state.SchemaVersion != ObservationState.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Observation state at {target.StateFilePath} has schemaVersion={state.SchemaVersion}, " +
                $"but this build only handles {ObservationState.CurrentSchemaVersion}. " +
                "Migration logic has not been implemented.");

        return state;
    }

    public async Task SaveAsync(
        ObservationTarget target,
        ObservationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(state);

        var dir = Path.GetDirectoryName(target.StateFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Temp file in the same directory as the target so the rename is atomic
        // (rename across filesystems is not).
        var tempPath = target.StateFilePath + ".tmp";

        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        {
            state.SchemaVersion = ObservationState.CurrentSchemaVersion;
            await JsonSerializer.SerializeAsync(
                stream, state, ObservationStateJsonOptions.Instance, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Atomic rename: readers see either the old file or the new one, never
        // a partially-written file. File.Move with overwrite is atomic on the
        // .NET runtimes we target.
        File.Move(tempPath, target.StateFilePath, overwrite: true);

        logger.LogDebug(
            "Observation: wrote state for target {Target} ({Candidates} candidates, {Theories} theories)",
            target.Name, state.Candidates.Count, state.Theories.Count);
    }
}
