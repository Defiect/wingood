using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Wingood.Core;

namespace Wingood;

internal sealed class RunRecorder : IDisposable
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly StreamWriter writer;
    private readonly List<RunEvent> events = [];
    private readonly Dictionary<string, (string State, int Bucket, double Time, bool Complete)> lastProgress = [];
    private readonly HashSet<string> completed = [];
    private Exception? loggingError;
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions EventOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public RunRecorder(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath))
            throw new ArgumentException($"Output path already exists: {DirectoryPath}. Choose a new directory.");
        Directory.CreateDirectory(DirectoryPath);
        writer = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string DirectoryPath { get; }

    public void Progress(PackageCandidate package, UpgradeProgress progress)
    {
        lock (gate)
        {
            if (completed.Contains(package.Key)) return;
            var time = clock.Elapsed.TotalSeconds;
            var bucket = (int)(Math.Clamp(progress.DownloadProgress, 0, 1) * 10);
            var hasPrevious = lastProgress.TryGetValue(package.Key, out var previous);
            var downloadComplete = progress.State == "Downloading" && progress.DownloadProgress >= 1;
            if (hasPrevious && previous.State == progress.State && time - previous.Time < .2
                && !(downloadComplete && !previous.Complete)) return;
            Append(new RunEvent(DateTimeOffset.UtcNow, time, "progress", package.Key, package.Id, progress));
            lastProgress[package.Key] = (progress.State, bucket, time, downloadComplete);
        }
    }

    public void Complete(PackageOutcome outcome)
    {
        lock (gate)
        {
            completed.Add(outcome.Package.Key);
            var time = clock.Elapsed.TotalSeconds;
            Append(new RunEvent(DateTimeOffset.UtcNow, time, "completed", outcome.Package.Key, outcome.Package.Id, Result: outcome.Result));
        }
    }

    public RunMetrics Finish(IReadOnlyList<PackageOutcome> outcomes, bool cancellationRequested)
    {
        lock (gate)
        {
            clock.Stop();
            var metrics = Telemetry.Analyze(events, clock.Elapsed.TotalSeconds);
            SaveJson("results.json", new
            {
                runFinished = DateTimeOffset.UtcNow,
                cancellationRequested,
                loggingError = loggingError?.Message,
                outcomes,
                metrics,
                measurement = "Timestamped WinGet API states. Installing includes preparation/waiting, not just vendor execution. Repeated progress coalesced to at most 5 samples per second per package; state transitions always retained.",
            });
            if (loggingError is not null) SaveJson("recovered-events.json", events);
            return metrics;
        }
    }

    public void SaveJson(string name, object value)
    {
        var destination = Path.Combine(DirectoryPath, name);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, destination, overwrite: true);
    }

    private void Append(RunEvent entry)
    {
        events.Add(entry);
        if (loggingError is not null) return;
        try { writer.WriteLine(JsonSerializer.Serialize(entry, EventOptions)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            loggingError = error;
            WriteConsole($"Event log write failed: {error.Message}. Keeping events in memory while active operations finish.");
        }
    }

    private static void WriteConsole(string value)
    {
        try { Console.WriteLine(new string(value.Where(c => !char.IsControl(c)).ToArray())); }
        catch (IOException) { /* A closed output pipe must not interrupt active installers. */ }
    }

    public void Dispose() => writer.Dispose();
}
