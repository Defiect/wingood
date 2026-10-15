namespace Wingood.Core;

public sealed record PackageCandidate(
    string Key, string Id, string Name, string InstalledVersion,
    string AvailableVersion, string InstalledScope, string InstalledType);

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
            throw new ArgumentException("Specify one or more exact package IDs. Run 'list' to see candidates.");

        var selected = new List<PackageCandidate>();
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matches = candidates.Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                && (scope is null || p.InstalledScope.Equals(scope, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length == 0)
                throw new ArgumentException($"No eligible upgrade found for '{id}'. It may be current, pinned, have an unknown version, or not match the selected scope/source.");
            if (matches.Length > 1)
                throw new ArgumentException($"'{id}' matches multiple installed copies. Use --scope user or --scope machine to narrow the selection; unresolved duplicates are not upgraded.");
            selected.Add(matches[0]);
        }
        return selected;
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
