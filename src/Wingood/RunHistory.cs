using System.Text.Json;
using Wingood.Core;

namespace Wingood;

internal sealed record SavedRun(IReadOnlyList<PackageCandidate> Packages);
internal sealed record SavedResults(IReadOnlyList<PackageOutcome> Outcomes);

internal sealed class RunHistory(string? directory = null)
{
    public string Root { get; } = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wingood", "runs");

    public string NewDirectory(string runId)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{runId}");
    }

    public IReadOnlyList<PackageCandidate> FailedPackages()
    {
        if (!Directory.Exists(Root)) return [];
        var last = Directory.EnumerateDirectories(Root).OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        if (last is null) return [];
        var resultPath = Path.Combine(last, "results.json");
        if (File.Exists(resultPath))
        {
            try
            {
                var results = JsonSerializer.Deserialize<SavedResults>(File.ReadAllText(resultPath), RunRecorder.JsonOptions);
                if (results?.Outcomes is not null)
                    return results.Outcomes.Where(outcome => !outcome.Result.Succeeded).Select(outcome => outcome.Package).ToArray();
            }
            catch (Exception error) when (error is JsonException or IOException) { /* Recover from the durable plan and completions below. */ }
        }
        // A process killed before it could save its final summary may still have
        // completed some operations. Read the durable completions before retrying.
        var planPath = Path.Combine(last, "plan.json");
        if (!File.Exists(planPath)) throw new InvalidOperationException("The last run has no readable plan. Select apps explicitly instead.");
        var plan = JsonSerializer.Deserialize<SavedRun>(File.ReadAllText(planPath), RunRecorder.JsonOptions)
            ?? throw new InvalidOperationException("The last run plan is empty.");
        if (plan.Packages is null) throw new InvalidOperationException("The last run has no readable package selection.");
        var succeeded = new HashSet<string>(StringComparer.Ordinal);
        var eventsPath = Path.Combine(last, "events.jsonl");
        if (File.Exists(eventsPath))
        {
            foreach (var line in File.ReadLines(eventsPath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<RunEvent>(line, RunRecorder.JsonOptions);
                    if (entry is { Kind: "completed", Result.Succeeded: true }) succeeded.Add(entry.PackageKey);
                }
                catch (JsonException) { /* A final line can be incomplete after a crash. */ }
            }
        }
        return plan.Packages.Where(package => !succeeded.Contains(package.Key)).ToArray();
    }

    public void Prune()
    {
        if (!Directory.Exists(Root)) return;
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(Root).OrderDescending(StringComparer.Ordinal).Skip(30))
        {
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(resolved, recursive: true);
        }
    }
}
