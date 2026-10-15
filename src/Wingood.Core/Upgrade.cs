namespace Wingood.Core;

public sealed record PackageCandidate(
    string Key, string Id, string Name, string InstalledVersion,
    string AvailableVersion, string InstalledScope, string InstalledType,
    string Source = "winget");

public sealed record UpgradeProgress(
    string State, ulong BytesDownloaded = 0, ulong BytesRequired = 0,
    double DownloadProgress = 0, double InstallationProgress = 0);

public sealed record UpgradeResult(
    string Status, string? ErrorCode = null, uint? InstallerErrorCode = null,
    bool RebootRequired = false, string? Message = null)
{
    public bool Succeeded => Status == "Ok";
}

public sealed record PackageOutcome(PackageCandidate Package, UpgradeResult Result);

public interface IUpgradeBackend
{
    Task<UpgradeResult> UpgradeAsync(
        PackageCandidate package, IProgress<UpgradeProgress> progress, CancellationToken cancellationToken);
}

public static class PackageSelection
{
    public static IReadOnlyList<PackageCandidate> Select(
        IReadOnlyList<PackageCandidate> candidates, IReadOnlyList<string> ids, string? scope = null)
    {
        if (ids.Count == 0)
            throw new ArgumentException("Specify one or more package IDs or names. Run 'upgrade' to see available updates.", nameof(ids));

        var selected = new List<PackageCandidate>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in ids)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Package IDs and names cannot be empty.", nameof(ids));

            var inScope = candidates.Where(candidate =>
                scope is null || candidate.InstalledScope.Equals(scope, StringComparison.OrdinalIgnoreCase));
            var eligible = inScope.ToArray();

            // Exact package IDs take precedence over names, then partial matches.
            var matches = eligible.Where(candidate =>
                candidate.Id.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                matches = eligible.Where(candidate =>
                    candidate.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            if (matches.Length == 0)
            {
                matches = eligible.Where(candidate =>
                    candidate.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (matches.Length == 0)
                throw new ArgumentException($"No eligible upgrade matches '{query}'. It may be current, pinned, have an unknown version, or not match the selected scope/source.", nameof(ids));
            if (matches.Length > 1)
                throw new ArgumentException(FormatAmbiguous(query, matches, scope), nameof(ids));

            if (selectedKeys.Add(matches[0].Key))
                selected.Add(matches[0]);
        }
        return selected;
    }

    /// <summary>
    /// Binds WinGet's eligible upgrade rows to installed package candidates.
    /// Every identity and version component must agree exactly or the batch is
    /// rejected rather than risking an update to a different catalog entry.
    /// </summary>
    public static IReadOnlyList<PackageCandidate> BindBulk(
        IReadOnlyList<PackageCandidate> candidates, IReadOnlyList<NativeUpgrade> entries)
    {
        var bound = new List<PackageCandidate>(entries.Count);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidOperationException("WinGet returned an eligible upgrade with an empty package ID.");

            var matches = candidates.Where(candidate =>
                candidate.Id.Equals(entry.Id, StringComparison.Ordinal)
                && candidate.Source.Equals(entry.Source, StringComparison.Ordinal)
                && candidate.InstalledVersion.Equals(entry.InstalledVersion, StringComparison.Ordinal)
                && candidate.AvailableVersion.Equals(entry.AvailableVersion, StringComparison.Ordinal)).ToArray();

            if (matches.Length == 0)
                throw new InvalidOperationException(
                    $"Could not bind WinGet's eligible row '{entry.Id}' from source '{entry.Source}' " +
                    $"({entry.InstalledVersion} -> {entry.AvailableVersion}) to one installed package candidate.");
            if (matches.Length > 1)
                throw new InvalidOperationException(
                    $"WinGet's eligible row '{entry.Id}' from source '{entry.Source}' " +
                    "matches multiple installed package candidates with the same versions.");
            if (!keys.Add(matches[0].Key))
                throw new InvalidOperationException(
                    $"WinGet returned duplicate eligible rows for '{entry.Id}' from source '{entry.Source}'.");

            bound.Add(matches[0]);
        }

        return bound;
    }

    /// <summary>Validates exclusions against all eligible candidates, then removes them from a selection.</summary>
    public static IReadOnlyList<PackageCandidate> Exclude(
        IReadOnlyList<PackageCandidate> selected,
        IReadOnlyList<PackageCandidate> allCandidates,
        IReadOnlyList<string> queries,
        string? scope = null)
    {
        if (queries.Count == 0)
            return selected;

        // Resolve the full exclusion list first so an invalid later query never
        // produces a partially filtered batch.
        var excluded = Select(allCandidates, queries, scope)
            .Select(candidate => candidate.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selected.Where(candidate => !excluded.Contains(candidate.Key)).ToArray();
    }

    private static string FormatAmbiguous(
        string query, IReadOnlyList<PackageCandidate> matches, string? scope)
    {
        var details = string.Join("; ", matches.Select(candidate =>
            $"{candidate.Name} ({candidate.Id}, {candidate.Source}, {candidate.InstalledScope}, " +
            $"{candidate.InstalledVersion} -> {candidate.AvailableVersion})"));
        var scopeHint = scope is null
            ? " Use --scope user or --scope machine where that distinguishes installed copies."
            : string.Empty;
        return $"'{query}' is ambiguous: {details}.{scopeHint}";
    }
}

// Keeps progress delivery synchronous; Progress<T> posts callbacks to a thread pool,
// which can reorder state changes relative to operation completion.
public sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

public sealed class BatchRunner(IUpgradeBackend backend)
{
    public async Task<IReadOnlyList<PackageOutcome>> RunAsync(
        IReadOnlyList<PackageCandidate> packages, int inFlight,
        Action<PackageCandidate, UpgradeProgress> onProgress,
        Action<PackageOutcome> onCompleted, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inFlight, 1);
        var active = new List<Task<PackageOutcome>>();
        var completed = new Dictionary<string, PackageOutcome>();
        var next = 0;

        while (next < packages.Count || active.Count > 0)
        {
            while (!cancellationToken.IsCancellationRequested && next < packages.Count && active.Count < inFlight)
                active.Add(RunOneAsync(packages[next++]));

            if (active.Count == 0)
                break;

            var done = await Task.WhenAny(active).ConfigureAwait(false);
            active.Remove(done);
            var outcome = await done.ConfigureAwait(false);
            completed.Add(outcome.Package.Key, outcome);
            onCompleted(outcome);
        }

        foreach (var package in packages.Skip(next))
        {
            var outcome = new PackageOutcome(package, new UpgradeResult("NotStarted", Message: "Cancelled before submission."));
            completed.Add(package.Key, outcome);
            onCompleted(outcome);
        }

        return packages.Select(p => completed[p.Key]).ToArray();

        async Task<PackageOutcome> RunOneAsync(PackageCandidate package)
        {
            onProgress(package, new UpgradeProgress("Submitted"));
            try
            {
                var result = await backend.UpgradeAsync(package,
                    new InlineProgress<UpgradeProgress>(progress => onProgress(package, progress)), cancellationToken).ConfigureAwait(false);
                return new PackageOutcome(package, result);
            }
            catch (OperationCanceledException)
            {
                return new PackageOutcome(package, new UpgradeResult("Cancelled"));
            }
            catch (Exception error)
            {
                return new PackageOutcome(package, new UpgradeResult("Exception", $"0x{error.HResult:X8}", Message: error.Message));
            }
        }
    }
}
