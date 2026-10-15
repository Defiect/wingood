using System.Diagnostics;
using System.Text.Json;
using Wingood.Core;

namespace Wingood;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var processStarted = DateTimeOffset.UtcNow;
        var processClock = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
            Console.Error.WriteLine("Cancellation requested. Waiting for active WinGet operations to settle; running installers may finish.");
        };
        Console.CancelKeyPress += handler;
        try
        {
            var command = CommandLine.Parse(args);
            if (command.Command == "help")
            {
                Console.WriteLine(Help);
                return 0;
            }

            Console.Error.WriteLine($"Reading installed packages and '{command.Source}' updates...");
            var backend = new WinGetBackend(command.AcceptAgreements);
            var candidates = await backend.DiscoverAsync(command.Source, command.Scope, command.Ids, cancellation.Token);
            var selected = command.Ids.Count == 0 ? candidates : PackageSelection.Select(candidates, command.Ids, command.Scope);
            if (command.Command == "list" || command.DryRun)
            {
                if (command.Json)
                    Console.WriteLine(JsonSerializer.Serialize(selected, RunRecorder.JsonOptions));
                else
                {
                    PrintPackages(selected);
                    Console.WriteLine(command.DryRun ? "Dry run: no downloads or upgrades were submitted." : "Candidate updates; explicit IDs are required to upgrade. Unknown versions and user-pinned updates are excluded.");
                }
                if (command.Output is not null)
                {
                    using var recorder = new RunRecorder(command.Output);
                    recorder.SaveJson("plan.json", new { dryRun = true, command.Source, command.InFlight, packages = selected });
                }
                return 0;
            }

            PrintPackages(selected);
            var directory = command.Output ?? Path.Combine("artifacts", "runs", $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{backend.RunId[..8]}");
            using var run = new RunRecorder(directory);
            run.SaveJson("plan.json", new
            {
                runId = backend.RunId,
                processStarted,
                discoveryAndSelectionSeconds = processClock.Elapsed.TotalSeconds,
                started = DateTimeOffset.UtcNow,
                command.Source,
                command.InFlight,
                command.AcceptAgreements,
                packages = selected,
                engine = "Installed WinGet via out-of-process COM API",
                sdk = "1.30.100-preview",
                os = Environment.OSVersion.VersionString,
            });
            Console.WriteLine($"Submitting at most {command.InFlight} outstanding operations. WinGet controls downloads and installation order.");
            Console.WriteLine($"Run files: {run.DirectoryPath}");
            var outcomes = await new BatchRunner(backend).RunAsync(selected, command.InFlight, run.Progress, run.Complete, cancellation.Token);
            var metrics = run.Finish(outcomes, cancellation.IsCancellationRequested);
            Console.WriteLine($"Finished in {metrics.ElapsedSeconds:F1}s: {outcomes.Count(o => o.Result.Succeeded)} succeeded, {outcomes.Count(o => !o.Result.Succeeded)} other outcomes.");
            Console.WriteLine($"Downloading/installing API-state overlap (includes waiting): {metrics.DownloadingInstallingOverlapSeconds:F1}s. Timeline: {Path.Combine(run.DirectoryPath, "timeline.svg")}");
            if (outcomes.Any(o => o.Result.RebootRequired))
                Console.WriteLine("At least one package reports that a reboot is required.");
            return cancellation.IsCancellationRequested ? 130 : outcomes.All(o => o.Result.Succeeded) ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"{error.Message} (0x{error.HResult:X8})");
            if (error.HResult == unchecked((int)0x800706BA))
                Console.Error.WriteLine("Check WinGet engine/SDK compatibility, especially in elevated terminals. See the README's activation-transport note.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static void PrintPackages(IReadOnlyList<PackageCandidate> packages)
    {
        var idWidth = Math.Max(10, packages.Select(p => p.Id.Length).DefaultIfEmpty(10).Max());
        var oldWidth = Math.Max(9, packages.Select(p => p.InstalledVersion.Length).DefaultIfEmpty(9).Max());
        var newWidth = Math.Max(9, packages.Select(p => p.AvailableVersion.Length).DefaultIfEmpty(9).Max());
        Console.WriteLine($"{"ID".PadRight(idWidth)}  {"Installed".PadRight(oldWidth)}  {"Available".PadRight(newWidth)}  Scope / installed type");
        foreach (var p in packages)
            Console.WriteLine(Clean($"{p.Id.PadRight(idWidth)}  {p.InstalledVersion.PadRight(oldWidth)}  {p.AvailableVersion.PadRight(newWidth)}  {p.InstalledScope} / {p.InstalledType}"));
        Console.WriteLine($"{packages.Count} candidate(s).");
        static string Clean(string text) => new(text.Where(c => !char.IsControl(c)).ToArray());
    }

    private const string Help = """
        wingood — download ahead while WinGet installs

        list [ID ...] [--source NAME] [--scope user|machine] [--json]
        upgrade ID [ID ...] [--dry-run] [--json] [--source NAME]
                [--scope user|machine] [--in-flight 1..16]
                [--accept-package-agreements] [--output NEW_DIRECTORY]

        No arguments lists eligible updates. IDs match exactly, ignoring case.
        --dry-run discovers and validates the whole selection without updating apps.
        --in-flight defaults to 4; it bounds outstanding API requests, not installers.
        WinGet currently allows up to 3 downloads and 1 install operation at a time.
        Installer execution uses silent mode, hash verification and normal dependencies.
        --all is not supported: explicit-upgrade exclusions aren't exposed by this API.

        Runs save plan.json, events.jsonl, results.json and timeline.svg under
        artifacts/runs unless --output specifies a new directory.
        Ctrl+C cancels queued/download work and waits for active operations to settle.
        Exit codes: 0 success, 1 failure, 2 invalid request, 130 cancellation.
        """;
}
