using System.Net;
using System.Text;

namespace Wingood.Core;

public sealed record RunEvent(DateTimeOffset Timestamp, double ElapsedSeconds, string Kind,
    string PackageKey, string PackageId, UpgradeProgress? Progress = null, UpgradeResult? Result = null);
public sealed record StateInterval(string PackageKey, string PackageId, string State, double Start, double End);
public sealed record DownloadCompletion(string PackageKey, double ElapsedSeconds, ulong Bytes);
public sealed record RunMetrics(double ElapsedSeconds, int MaximumDownloadingStates, int MaximumInstallingStates,
    double DownloadingInstallingOverlapSeconds, IReadOnlyList<StateInterval> Intervals,
    IReadOnlyList<DownloadCompletion> CompletedDownloads);

public static class Telemetry
{
    public static RunMetrics Analyze(IReadOnlyList<RunEvent> events, double elapsedSeconds)
    {
        var intervals = new List<StateInterval>();
        var active = new Dictionary<string, (string Id, string State, double Start)>();
        foreach (var entry in events.OrderBy(e => e.ElapsedSeconds))
        {
            var state = entry.Kind == "progress" ? entry.Progress?.State : null;
            if (active.TryGetValue(entry.PackageKey, out var previous))
            {
                if (state == previous.State) continue;
                intervals.Add(new(entry.PackageKey, previous.Id, previous.State, previous.Start, entry.ElapsedSeconds));
                active.Remove(entry.PackageKey);
            }
            if (state is not null) active[entry.PackageKey] = (entry.PackageId, state, entry.ElapsedSeconds);
        }
        foreach (var (key, previous) in active)
            intervals.Add(new(key, previous.Id, previous.State, previous.Start, elapsedSeconds));

        var boundaries = intervals.Where(i => i.State is "Downloading" or "Installing")
            .SelectMany(i => new[] { (Time: i.Start, State: i.State, Delta: 1), (Time: i.End, State: i.State, Delta: -1) })
            .GroupBy(b => b.Time).OrderBy(g => g.Key);
        var downloading = 0;
        var installing = 0;
        var maximumDownloads = 0;
        var maximumInstalls = 0;
        var previousTime = 0d;
        var overlap = 0d;
        foreach (var boundary in boundaries)
        {
            if (downloading > 0 && installing > 0) overlap += boundary.Key - previousTime;
            foreach (var change in boundary)
            {
                if (change.State == "Downloading") downloading += change.Delta;
                else installing += change.Delta;
            }
            maximumDownloads = Math.Max(maximumDownloads, downloading);
            maximumInstalls = Math.Max(maximumInstalls, installing);
            previousTime = boundary.Key;
        }
        var completions = events.Where(e => e.Progress is { State: "Downloading", DownloadProgress: >= 1, BytesRequired: > 0 } p
                && p.BytesDownloaded >= p.BytesRequired)
            .GroupBy(e => e.PackageKey).Select(g => g.MinBy(e => e.ElapsedSeconds)!)
            .Select(e => new DownloadCompletion(e.PackageKey, e.ElapsedSeconds, e.Progress!.BytesDownloaded)).ToArray();
        return new(elapsedSeconds, maximumDownloads, maximumInstalls, overlap, intervals, completions);
    }

    public static string CreateSvg(RunMetrics metrics, IReadOnlyList<PackageOutcome> outcomes)
    {
        const int left = 355;
        const int width = 1220;
        var height = 225 + outcomes.Count * 42;
        var chartWidth = width - left - 35;
        var duration = Math.Max(metrics.ElapsedSeconds, .001);
        var svg = new StringBuilder();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"WinGet API operation timeline\">");
        svg.AppendLine("<style>text{font-family:Segoe UI,Arial,sans-serif;fill:#26384c;font-size:13px}.title{font-size:24px;font-weight:600}.note{font-size:12px;fill:#526375}</style>");
        svg.AppendLine($"<rect width=\"{width}\" height=\"{height}\" fill=\"#fafbfd\"/>");
        Text(24, 36, $"WinGet API timeline · {metrics.ElapsedSeconds:F1}s", "title");
        Text(24, 63, $"{outcomes.Count(o => o.Result.Succeeded)} succeeded · {outcomes.Count(o => !o.Result.Succeeded)} other outcomes · Downloaded/waiting shown separately when completion counters are available");
        var legendX = 24;
        foreach (var state in new[] { "Queued", "Downloading", "Downloaded/waiting", "Installing", "PostInstall" })
        {
            svg.AppendLine($"<rect x=\"{legendX}\" y=\"85\" width=\"16\" height=\"12\" fill=\"{Color(state)}\"/>");
            Text(legendX + 22, 96, state);
            legendX += 190;
        }
        for (var tick = 0; tick <= 5; tick++)
        {
            var x = left + chartWidth * tick / 5d;
            svg.AppendLine(FormattableString.Invariant($"<path d=\"M{x:F2},115 V{height - 85}\" stroke=\"#dfe5ed\"/>"));
            Text(x, height - 61, $"{metrics.ElapsedSeconds * tick / 5:F1}s");
        }
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            var y = 122 + index * 42;
            var label = outcome.Package.Id.Length > 44 ? outcome.Package.Id[..41] + "…" : outcome.Package.Id;
            Text(24, y + 17, label);
            Text(24, y + 33, outcome.Result.Status, "note");
            foreach (var interval in metrics.Intervals.Where(i => i.PackageKey == outcome.Package.Key))
            {
                var completion = metrics.CompletedDownloads.FirstOrDefault(d => d.PackageKey == interval.PackageKey
                    && d.ElapsedSeconds >= interval.Start && d.ElapsedSeconds <= interval.End);
                if (interval.State == "Downloading" && completion is not null)
                {
                    Bar(interval.Start, completion.ElapsedSeconds, "Downloading");
                    Bar(completion.ElapsedSeconds, interval.End, "Downloaded/waiting");
                }
                else Bar(interval.Start, interval.End, interval.State);

                void Bar(double start, double end, string state)
                {
                    if (end <= start) return;
                    var x = left + chartWidth * start / duration;
                    var barWidth = Math.Max(.5, chartWidth * (end - start) / duration);
                    svg.AppendLine(FormattableString.Invariant($"<rect x=\"{x:F2}\" y=\"{y}\" width=\"{barWidth:F2}\" height=\"23\" fill=\"{Color(state)}\"><title>{WebUtility.HtmlEncode(outcome.Package.Id)}: {WebUtility.HtmlEncode(state)} {end - start:F3}s</title></rect>"));
                }
            }
        }
        Text(24, height - 32, "Observed API states, including preparation/waiting. Installing is not a native installer execution trace.", "note");
        Text(24, height - 13, "Downloading can include waiting if a completion sample is absent. Hover over a bar for its duration.", "note");
        svg.AppendLine("</svg>");
        return svg.ToString();

        void Text(double x, double y, string text, string? css = null) => svg.AppendLine(
            FormattableString.Invariant($"<text x=\"{x:F2}\" y=\"{y:F2}\" class=\"{css}\">{WebUtility.HtmlEncode(text)}</text>"));
        static string Color(string state) => state switch
        {
            "Downloading" => "#2878b5",
            "Downloaded/waiting" => "#b7cae4",
            "Installing" => "#26866d",
            "PostInstall" => "#c38413",
            "Finished" => "#97b6aa",
            _ => "#cdd5e0",
        };
    }
}
