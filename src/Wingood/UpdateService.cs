using System.Diagnostics;
using Spectre.Console;
using Wingood.Core;

namespace Wingood;

internal sealed record UpdateDiscovery(IUpgradeBackend Backend, IReadOnlyList<PackageCandidate> Candidates,
    IReadOnlyList<NativeUpgrade> Entries, IReadOnlyList<string> Warnings, string RunId,
    IReadOnlyDictionary<string, string>? Unavailable = null);

internal interface IUpdateCatalog
{
    Task<UpdateDiscovery> DiscoverAsync(SourceSettings settings, IReadOnlyList<string> queries,
        bool needsBulkEligibility, CancellationToken cancellationToken, bool acceptSourceAgreements = false);
}

internal sealed class UpdateService(Terminal terminal, IUpdateCatalog? catalog = null, RunHistory? historyOverride = null)
{
    private async Task<UpdateDiscovery> DiscoverAsync(SourceSettings settings, IReadOnlyList<string> queries,
        bool needsBulkEligibility, CancellationToken cancellationToken, bool acceptSourceAgreements = false)
    {
        if (catalog is not null) return await catalog.DiscoverAsync(settings, queries, needsBulkEligibility, cancellationToken, acceptSourceAgreements);
        terminal.Line("[grey]Checking available updates…[/]");
        var warnings = new List<string>();
        IReadOnlyList<string> sources = settings.NativeSource is { } source ? [source] : ["winget", "msstore"];
        var entries = new List<NativeUpgrade>();
        if (needsBulkEligibility)
        {
            try { entries.AddRange(await NativeCli.UpgradesAsync(settings.NativeSource, settings.Scope?.ToLowerInvariant(), acceptSourceAgreements, cancellationToken)); }
            catch (InvalidOperationException) when (settings.NativeSource is null)
            {
                // A failed source should not hide updates from a healthy source.
                var healthy = new List<string>();
                foreach (var candidateSource in sources)
                {
                    try
                    {
                        entries.AddRange(await NativeCli.UpgradesAsync(candidateSource, settings.Scope?.ToLowerInvariant(), acceptSourceAgreements, cancellationToken));
                        healthy.Add(candidateSource);
                    }
                    catch (InvalidOperationException error) { warnings.Add($"{candidateSource}: {error.Message}"); }
                }
                sources = healthy;
            }
        }
        var backend = new WinGetBackend(acceptAgreements: true);
        var search = needsBulkEligibility ? entries.Select(entry => entry.Id).Concat(queries).ToArray() : queries;
        var candidates = sources.Count == 0 || needsBulkEligibility && search.Count == 0
            ? [] : await backend.DiscoverAsync(sources, settings.Scope?.ToLowerInvariant(), search, cancellationToken, acceptSourceAgreements);
        warnings.AddRange(backend.SourceErrors);
        return new(backend, candidates, entries, warnings, backend.RunId, backend.Unavailable);
    }

    private static IReadOnlyList<PackageCandidate> Bind(UpdateDiscovery discovery, List<string> warnings)
    {
        var selected = new List<PackageCandidate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in discovery.Entries)
        {
            try
            {
                var package = PackageSelection.BindBulk(discovery.Candidates, [entry])[0];
                if (keys.Add(package.Key)) selected.Add(package);
                else warnings.Add($"Skipped duplicate listing for {entry.Id}.");
            }
            catch (InvalidOperationException error)
            {
                var reason = discovery.Unavailable?.GetValueOrDefault(entry.Id) ?? error.Message;
                warnings.Add($"Skipped {entry.Id}: {reason}");
            }
        }
        return selected;
    }

    public async Task<int> ListAsync(SourceSettings settings, CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(settings, [], needsBulkEligibility: true, cancellationToken);
        var warnings = discovery.Warnings.ToList();
        terminal.Packages(Bind(discovery, warnings));
        foreach (var warning in warnings) terminal.Warn(warning);
        return warnings.Count > 0 ? 1 : 0;
    }

    public async Task<int> UpgradeAsync(UpgradeSettings settings, bool retry, CancellationToken cancellationToken)
    {
        var history = historyOverride ?? new RunHistory();
        var failed = retry ? history.FailedPackages() : [];
        if (retry && settings.NativeSource is { } source) failed = failed.Where(package => package.Source == source).ToArray();
        if (retry && failed.Count == 0) { terminal.Line("[green]No failed apps to retry.[/]"); return 0; }
        var queries = retry ? failed.Select(package => package.Id).ToArray() : settings.Apps;
        var isBulk = !retry && settings.Apps.Length == 0;
        var clock = Stopwatch.StartNew();
        var acceptSources = !settings.DryRun && (retry || !isBulk || settings.Yes || settings.All);
        var discovery = await DiscoverAsync(settings, queries.Concat(settings.Excludes).ToArray(), isBulk, cancellationToken, acceptSources);
        var warnings = discovery.Warnings.ToList();
        IReadOnlyList<PackageCandidate> selected;
        if (retry)
        {
            var refreshed = new List<PackageCandidate>();
            foreach (var old in failed)
            {
                var matches = discovery.Candidates.Where(package => package.Id == old.Id && package.Source == old.Source
                    && package.InstalledScope == old.InstalledScope).ToArray();
                if (matches.Length == 1) refreshed.Add(matches[0]);
                else if (matches.Length > 1) warnings.Add($"Skipped ambiguous installed copies of {old.Id}.");
                else terminal.Line($"[grey]{Terminal.Text(old.Name)} has no eligible update from {Terminal.Text(old.Source)} now.[/]");
            }
            selected = refreshed.DistinctBy(package => package.Key).ToArray();
        }
        else
        {
            try { selected = isBulk ? Bind(discovery, warnings) : PackageSelection.Select(discovery.Candidates, settings.Apps, settings.Scope); }
            catch (ArgumentException error) when (warnings.Count > 0)
            {
                foreach (var warning in warnings) terminal.Warn(warning);
                terminal.Error(error.Message);
                return 1;
            }
        }
        selected = PackageSelection.Exclude(selected, discovery.Candidates, settings.Excludes, settings.Scope);
        terminal.Packages(selected);
        foreach (var warning in warnings) terminal.Warn(warning);
        if (settings.DryRun)
        {
            terminal.Line("[cyan]Dry run. No downloads or installations were submitted.[/]");
            return warnings.Count > 0 ? 1 : 0;
        }
        if (selected.Count == 0) return warnings.Count > 0 ? 1 : 0;
        if (isBulk && !settings.Yes && !settings.All)
        {
            if (Console.IsInputRedirected)
                throw new ArgumentException("Use --yes or bare wg to update without interactive input.");
            terminal.Line("Proceeding accepts package and source agreements.");
            terminal.Console.Markup("[bold]Install these updates?[/] [green][[Y/n]][/] ");
            var reply = Console.ReadLine();
            if (reply is null) throw new ArgumentException("Input ended before confirmation. Use --yes or bare wg for unattended updates.");
            if (!Confirmation.Accepts(reply, interactive: true)) { terminal.Line("Updates declined."); return 0; }
            if (discovery.Warnings.Count > 0)
            {
                // A first-use source may have been unreadable before consent.
                // After yes, check it again with source agreements accepted.
                discovery = await DiscoverAsync(settings, settings.Excludes, needsBulkEligibility: true, cancellationToken, acceptSourceAgreements: true);
                warnings = discovery.Warnings.ToList();
                selected = PackageSelection.Exclude(Bind(discovery, warnings), discovery.Candidates, settings.Excludes, settings.Scope);
                terminal.Packages(selected);
                foreach (var warning in warnings) terminal.Warn(warning);
                if (selected.Count == 0) return warnings.Count > 0 ? 1 : 0;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var directory = history.NewDirectory(discovery.RunId);
        using var recorder = new RunRecorder(directory);
        var inFlight = settings.InFlight ?? Math.Max(1, selected.Count);
        recorder.SaveJson("plan.json", new
        {
            runId = discovery.RunId,
            started = DateTimeOffset.UtcNow,
            discoveryAndSelectionSeconds = clock.Elapsed.TotalSeconds,
            inFlight,
            packages = selected,
            source = settings.NativeSource,
            acceptedAgreements = true,
            sdk = "1.30.100-preview",
        });
        terminal.Line($"[cyan]Submitting {selected.Count} updates{(settings.InFlight is null ? "" : $", at most {inFlight} outstanding")}. WinGet controls download and installation order.[/]");
        var outcomes = await terminal.RunAsync(discovery.Backend, selected, inFlight, recorder, cancellationToken);
        var metrics = recorder.Finish(outcomes, cancellationToken.IsCancellationRequested);
        var failures = outcomes.Where(outcome => !outcome.Result.Succeeded).ToArray();
        terminal.Line($"[green]{outcomes.Count - failures.Length} succeeded[/], [red]{failures.Length} failed or cancelled[/] in {metrics.ElapsedSeconds:F1}s.");
        foreach (var failure in failures)
        {
            terminal.Error($"{failure.Package.Name}: {failure.Result.Status} {failure.Result.ErrorCode} {failure.Result.Message}");
            if (failure.Result.InstallerErrorCode is { } installerCode) terminal.Warn($"Installer exit code: {installerCode}");
        }
        if (failures.Length > 0) terminal.Line("[yellow]Run wingood retry to retry these apps.[/]");
        if (outcomes.Any(outcome => outcome.Result.RebootRequired)) terminal.Warn("At least one app reports that a reboot is required.");
        terminal.Line($"[grey]Run record: {Terminal.Text(directory)}[/]");
        try { history.Prune(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { terminal.Warn($"Could not remove old run records: {error.Message}"); }
        return cancellationToken.IsCancellationRequested ? 130 : failures.Length > 0 || warnings.Count > 0 ? 1 : 0;
    }
}
