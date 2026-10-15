using System.Text.Json;
using Microsoft.Management.Deployment;
using Wingood.Core;

namespace Wingood;

internal sealed class WinGetBackend(bool acceptAgreements) : IUpgradeBackend
{
    private readonly PackageManager manager = new();
    private readonly Dictionary<string, (CatalogPackage Package, PackageVersionId Version)> handles = [];

    public async Task<IReadOnlyList<PackageCandidate>> DiscoverAsync(string sourceName, string? scope, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var source = manager.GetPackageCatalogByName(sourceName)
            ?? throw new ArgumentException($"Source '{sourceName}' is not registered with WinGet.");
        var options = new CreateCompositePackageCatalogOptions
        {
            CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs,
            InstalledScope = scope switch
            {
                "user" => PackageInstallScope.User,
                "machine" => PackageInstallScope.System,
                _ => PackageInstallScope.Any,
            },
        };
        options.Catalogs.Add(source);
        var connection = await manager.CreateCompositePackageCatalog(options).ConnectAsync().AsTask(cancellationToken);
        if (connection.Status != ConnectResultStatus.Ok)
            throw new InvalidOperationException($"Catalog connection failed: {connection.Status}. Check 'winget source list' and source agreements with WinGet.");

        var found = await connection.PackageCatalog.FindPackagesAsync(new FindPackagesOptions()).AsTask(cancellationToken);
        if (found.Status != FindPackagesResultStatus.Ok)
            throw new InvalidOperationException($"Package search failed: {found.Status}.");

        var requested = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PackageCandidate>();
        for (var i = 0; i < found.Matches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = found.Matches[i].CatalogPackage;
            // Applicability can fetch manifests; only evaluate the requested IDs.
            if (requested.Count > 0 && !requested.Contains(package.Id)) continue;
            var installed = package.InstalledVersion;
            if (installed is null || string.IsNullOrWhiteSpace(installed.Version)
                || installed.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || !package.IsUpdateAvailable)
                continue;

            // DefaultInstallVersion can fall back to an inapplicable version when no
            // update exists. Only inspect it after IsUpdateAvailable succeeds.
            var target = package.DefaultInstallVersion;
            if (target is null)
                continue;
            PackageVersionId? versionId = null;
            for (var v = 0; v < package.AvailableVersions.Count; v++)
            {
                var version = package.AvailableVersions[v];
                if (version.Version == target.Version && version.Channel == target.Channel
                    && version.PackageCatalogId == target.PackageCatalog.Info.Id)
                {
                    versionId = version;
                    break;
                }
            }
            if (versionId is null)
                throw new InvalidOperationException($"Cannot bind the selected version of {package.Id} to its source.");

            var installedScope = installed.GetMetadata(PackageVersionMetadataField.InstalledScope).ToLowerInvariant();
            if (installedScope == "system") installedScope = "machine";
            var candidate = new PackageCandidate($"{sourceName}:{i}", package.Id, package.Name,
                installed.Version, target.Version, installedScope,
                installed.GetMetadata(PackageVersionMetadataField.InstallerType));
            handles.Add(candidate.Key, (package, versionId));
            candidates.Add(candidate);
        }
        return candidates.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<UpgradeResult> UpgradeAsync(PackageCandidate package, IProgress<UpgradeProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = handles[package.Key];
        var options = new InstallOptions
        {
            PackageVersionId = handle.Version,
            PackageInstallMode = PackageInstallMode.Silent,
            AcceptPackageAgreements = acceptAgreements,
            AllowHashMismatch = false,
            AllowUpgradeToUnknownVersion = false,
            SkipDependencies = false,
            Force = false,
            CorrelationData = JsonSerializer.Serialize(new { tool = "wingood", package.Id, runId = RunId }),
        };
        var operation = manager.UpgradePackageAsync(handle.Package, options);
        operation.Progress = (_, value) => progress.Report(new UpgradeProgress(
            value.State.ToString(), value.BytesDownloaded, value.BytesRequired,
            value.DownloadProgress, value.InstallationProgress));

        // Request cancellation, then wait for the native operation to settle. An
        // installer already running cannot be stopped or rolled back by this API.
        using var registration = cancellationToken.Register(() =>
        {
            try { operation.Cancel(); }
            catch (Exception) { /* It may already have completed. Still await its result. */ }
        });
        try
        {
            var result = await operation.AsTask();
            return new UpgradeResult(result.Status.ToString(),
                result.ExtendedErrorCode is { HResult: not 0 } error ? $"0x{error.HResult:X8}" : null,
                result.Status == InstallResultStatus.InstallError ? result.InstallerErrorCode : null,
                result.RebootRequired, result.ExtendedErrorCode?.HResult == unchecked((int)0x8A15007D)
                    ? "WinGet prohibits this per-user uninstall step in an elevated process. Retry this package from a normal terminal."
                    : result.ExtendedErrorCode?.Message);
        }
        finally
        {
            operation.Close();
        }
    }

    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
}
