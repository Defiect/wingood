using System.Globalization;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wingood.Core;

namespace Wingood.Tests;

[TestClass]
public sealed class TelemetryTests
{
    [TestMethod]
    public void DownloadCompletionSeparatesWaitingWithoutChangingRawStateMetrics()
    {
        var doneDownloading = new RunEvent(DateTimeOffset.UnixEpoch.AddSeconds(2), 2, "progress", "A", "A",
            new("Downloading", 1000, 1000, 1));
        var metrics = Telemetry.Analyze([Progress("A", 0, "Downloading"), doneDownloading,
            Progress("B", 1, "Installing"), Done("B", 4), Progress("A", 8, "Installing"), Done("A", 10)], 10);
        Assert.HasCount(1, metrics.CompletedDownloads);
        Assert.AreEqual(2d, metrics.CompletedDownloads[0].ElapsedSeconds);
        Assert.AreEqual(3d, metrics.DownloadingInstallingOverlapSeconds);
        var package = new PackageCandidate("A", "A", "A", "1", "2", "user", "portable");
        var svg = Telemetry.CreateSvg(metrics, [new(package, new("Ok"))]);
        StringAssert.Contains(svg, "Downloaded/waiting 6.000s");
    }

    [TestMethod]
    public void MultiPackageTimelineCanBeRenderedForInspection()
    {
        var metrics = Telemetry.Analyze([
            Progress("A", 0, "Downloading"), Progress("B", 0, "Downloading"),
            Progress("C", 0, "Queued"), Progress("A", 4, "Installing"),
            Progress("B", 6, "Queued"), Progress("A", 15, "PostInstall"),
            Progress("B", 15, "Installing"), Done("A", 16), Progress("B", 18, "PostInstall"),
            Progress("C", 18, "Installing"), Done("B", 19), Done("C", 20),
        ], 21);
        var outcomes = new[]
        {
            new PackageOutcome(new("A", "Example.Editor", "Editor", "1", "2", "user", "inno"), new("Ok")),
            new PackageOutcome(new("B", "Example.Utility", "Utility", "1", "2", "machine", "msi"), new("Ok")),
            new PackageOutcome(new("C", "Example.CachedApp", "Cached App", "1", "2", "user", "exe"), new("InstallError")),
        };
        var svg = Telemetry.CreateSvg(metrics, outcomes);
        Assert.AreEqual(2d, metrics.DownloadingInstallingOverlapSeconds);
        Assert.IsNotNull(XDocument.Parse(svg).Root);
        if (Environment.GetEnvironmentVariable("WINGET_TEST_ARTIFACTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "synthetic-timeline.svg"), svg);
        }
    }

    [TestMethod]
    public void MeasuresOverlapWithoutDoubleCountingOrBoundarySpikes()
    {
        var metrics = Telemetry.Analyze([
            Progress("A", 0, "Downloading"), Progress("B", 1, "Downloading"),
            Progress("A", 2, "Installing"), Progress("B", 3, "Downloading"),
            Progress("B", 4, "Queued"), Progress("A", 8, "Finished"),
            Progress("B", 8, "Installing"), Done("A", 9), Done("B", 10),
        ], 10);
        Assert.AreEqual(2d, metrics.DownloadingInstallingOverlapSeconds);
        Assert.AreEqual(2, metrics.MaximumDownloadingStates);
        Assert.AreEqual(1, metrics.MaximumInstallingStates);
        Assert.HasCount(1, metrics.Intervals.Where(i => i.PackageId == "B" && i.State == "Downloading"));
    }

    [TestMethod]
    public void CachedOrFailedAttemptDoesNotInventDownloadTime()
    {
        var metrics = Telemetry.Analyze([Progress("A", 1, "Queued"), Progress("A", 2, "Installing"), Done("A", 3)], 4);
        Assert.AreEqual(0, metrics.MaximumDownloadingStates);
        Assert.AreEqual(0d, metrics.DownloadingInstallingOverlapSeconds);
        Assert.AreEqual(3d, metrics.Intervals.Max(i => i.End));
    }

    [TestMethod]
    public void SvgIsValidEscapedAndIndependentOfNumericLocale()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var package = new PackageCandidate("A", "Example.<&>", "Example", "1", "2", "user", "exe");
            var metrics = Telemetry.Analyze([Progress("A", 0, "Downloading"), Done("A", 1.5)], 2.5);
            var svg = Telemetry.CreateSvg(metrics, [new PackageOutcome(package, new UpgradeResult("Ok"))]);
            var doc = XDocument.Parse(svg);
            Assert.IsNotNull(doc.Root);
            Assert.IsTrue(svg.Contains("&lt;&amp;&gt;", StringComparison.Ordinal));
            Assert.IsFalse(svg.Contains("x=\"355,", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static RunEvent Progress(string id, double time, string state) => new(DateTimeOffset.UnixEpoch.AddSeconds(time), time, "progress", id, id, new(state));
    private static RunEvent Done(string id, double time) => new(DateTimeOffset.UnixEpoch.AddSeconds(time), time, "completed", id, id, Result: new("Ok"));
}
