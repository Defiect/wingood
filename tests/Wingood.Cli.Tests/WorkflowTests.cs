using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spectre.Console;
using Wingood.Core;

namespace Wingood.Cli.Tests;

[TestClass]
public sealed class WorkflowTests
{
    private static readonly PackageCandidate App = new("a", "Example.App", "Example [App]", "1", "2", "user", "portable");
    private readonly string root = Path.Combine(Path.GetTempPath(), $"wingood-workflow-test-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(root, recursive: true);
    }

    private (UpdateService Service, FakeCatalog Catalog, StringWriter Output) Create(
        IReadOnlyList<PackageCandidate>? packages = null, IReadOnlyList<NativeUpgrade>? entries = null, IReadOnlyList<string>? warnings = null,
        IReadOnlyDictionary<string, string>? unavailable = null)
    {
        packages ??= [App];
        entries ??= packages.Select(package => new NativeUpgrade(package.Name, package.Id, package.InstalledVersion, package.AvailableVersion, package.Source)).ToArray();
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Interactive = InteractionSupport.No, Out = new AnsiConsoleOutput(output) });
        console.Profile.Width = 10000;
        var catalog = new FakeCatalog(packages, entries, warnings ?? [], unavailable);
        return (new UpdateService(new Terminal(console), catalog, new RunHistory(root)), catalog, output);
    }

    [TestMethod]
    public async Task BulkAndExplicitDryRunsNeverSubmitOrCreateHistory()
    {
        foreach (var apps in new[] { Array.Empty<string>(), new[] { App.Id } })
        {
            var setup = Create();
            Assert.AreEqual(0, await setup.Service.UpgradeAsync(new UpgradeSettings { Apps = apps, DryRun = true, Yes = true }, false, CancellationToken.None));
            Assert.AreEqual(0, setup.Catalog.Backend.Submitted.Count);
            Assert.IsFalse(setup.Catalog.AcceptedSourceAgreements);
            Assert.IsFalse(Directory.Exists(root));
            StringAssert.Contains(setup.Output.ToString(), "Example [App]");
        }
    }

    [TestMethod]
    public async Task InvalidSelectionOrExclusionRejectsBeforeAnyOperation()
    {
        foreach (var settings in new[]
        {
            new UpgradeSettings { Apps = [App.Id, "Missing"], Yes = true },
            new UpgradeSettings { Excludes = [App.Id, "Missing"], Yes = true },
        })
        {
            var setup = Create();
            await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.UpgradeAsync(settings, false, CancellationToken.None));
            Assert.AreEqual(0, setup.Catalog.Backend.Submitted.Count);
        }
    }

    [TestMethod]
    public async Task AllSelectionSubmitsFullBatchAndReportsPartialSourceFailure()
    {
        var other = App with { Key = "b", Id = "Other.App", Name = "Other" };
        var setup = Create([App, other], warnings: ["store unavailable"]);
        Assert.AreEqual(1, await setup.Service.UpgradeAsync(new UpgradeSettings { All = true }, false, CancellationToken.None));
        Assert.IsTrue(setup.Catalog.AcceptedSourceAgreements);
        CollectionAssert.AreEqual(new[] { App.Id, other.Id }, setup.Catalog.Backend.Submitted.Select(package => package.Id).ToArray());
        StringAssert.Contains(setup.Output.ToString(), "store unavailable");
    }

    [TestMethod]
    public async Task UnboundBulkEntryIsSkippedWhileHealthyAppsContinue()
    {
        var setup = Create(entries: [new("Example", App.Id, "1", "2", "winget"), new("Unknown", "Unknown.App", "1", "2", "winget")]);
        Assert.AreEqual(1, await setup.Service.UpgradeAsync(new UpgradeSettings { Yes = true }, false, CancellationToken.None));
        Assert.HasCount(1, setup.Catalog.Backend.Submitted);
    }

    [TestMethod]
    public async Task ExplicitAppWithFailedSourceShowsSourceErrorAndReturnsIncompleteProcessing()
    {
        var setup = Create([], warnings: ["msstore connection failed"]);
        Assert.AreEqual(1, await setup.Service.UpgradeAsync(new UpgradeSettings { Apps = [App.Id], Yes = true }, false, CancellationToken.None));
        Assert.HasCount(0, setup.Catalog.Backend.Submitted);
        StringAssert.Contains(setup.Output.ToString(), "msstore connection failed");
    }

    [TestMethod]
    public async Task ConfirmedInapplicableVersionIsAnInformationalExclusion()
    {
        var setup = Create(entries: [new("Example", App.Id, "1", "2", "winget"), new("Other", "Other.App", "1", "2", "winget")],
            unavailable: new Dictionary<string, string> { ["Other.App"] = WinGetBackend.NoApplicableUpdate });
        Assert.AreEqual(0, await setup.Service.UpgradeAsync(new UpgradeSettings { Yes = true }, false, CancellationToken.None));
        Assert.HasCount(1, setup.Catalog.Backend.Submitted);
        StringAssert.Contains(setup.Output.ToString(), "Skipping Other.App");
    }

    [TestMethod]
    public async Task RetryRefreshesVersionsWithoutAddingOtherUpdatesOrSwitchingSources()
    {
        var storeCopy = App with { Key = "store", Source = "msstore" };
        SavePrevious([new(App, new("InstallError")), new(storeCopy, new("Ok"))]);
        var refreshed = App with { InstalledVersion = "2", AvailableVersion = "3" };
        var unrelated = App with { Key = "b", Id = "Other.App" };
        var setup = Create([refreshed, storeCopy, unrelated]);
        Assert.AreEqual(0, await setup.Service.UpgradeAsync(new UpgradeSettings(), true, CancellationToken.None));
        Assert.HasCount(1, setup.Catalog.Backend.Submitted);
        Assert.AreEqual("3", setup.Catalog.Backend.Submitted[0].AvailableVersion);
        Assert.AreEqual("winget", setup.Catalog.Backend.Submitted[0].Source);
        CollectionAssert.AreEqual(new[] { App.Id }, setup.Catalog.Queries.ToArray());
    }

    [TestMethod]
    public async Task RetryOmitsAlreadyCurrentAppsAndNoArgumentShowIsReadOnly()
    {
        SavePrevious([new(App, new("InstallError"))]);
        var setup = Create([]);
        Assert.AreEqual(0, await setup.Service.UpgradeAsync(new UpgradeSettings(), true, CancellationToken.None));
        Assert.HasCount(0, setup.Catalog.Backend.Submitted);
        var listed = Create();
        Assert.AreEqual(0, await listed.Service.ListAsync(new ShowSettings(), CancellationToken.None));
        Assert.HasCount(0, listed.Catalog.Backend.Submitted);
        Assert.IsFalse(listed.Catalog.AcceptedSourceAgreements);
    }

    [TestMethod]
    public void InterruptedHistoryRetainsOnlyUnfinishedOrFailedAppsAndPrunesToThirty()
    {
        var directory = new RunHistory(root).NewDirectory("last");
        using (var record = new RunRecorder(directory))
        {
            record.SaveJson("plan.json", new { packages = new[] { App, App with { Key = "b", Id = "Other.App" } } });
            record.Complete(new(App, new("Ok")));
        }
        File.WriteAllText(Path.Combine(directory, "results.json"), "{\"outcomes\":");
        Assert.AreEqual("Other.App", new RunHistory(root).FailedPackages().Single().Id);
        for (var index = 0; index < 35; index++) Directory.CreateDirectory(Path.Combine(root, $"20000101-{index:D3}"));
        new RunHistory(root).Prune();
        Assert.HasCount(30, Directory.GetDirectories(root));
    }

    private void SavePrevious(IReadOnlyList<PackageOutcome> outcomes)
    {
        var directory = Path.Combine(root, "20000101-previous");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { outcomes }, RunRecorder.JsonOptions));
    }

    private sealed class FakeBackend : IUpgradeBackend
    {
        public List<PackageCandidate> Submitted { get; } = [];
        public Task<UpgradeResult> UpgradeAsync(PackageCandidate package, IProgress<UpgradeProgress> progress, CancellationToken cancellationToken)
        {
            Submitted.Add(package);
            return Task.FromResult(new UpgradeResult("Ok"));
        }
    }

    private sealed class FakeCatalog(IReadOnlyList<PackageCandidate> packages, IReadOnlyList<NativeUpgrade> entries,
        IReadOnlyList<string> warnings, IReadOnlyDictionary<string, string>? unavailable) : IUpdateCatalog
    {
        public FakeBackend Backend { get; } = new();
        public IReadOnlyList<string> Queries { get; private set; } = [];
        public bool AcceptedSourceAgreements { get; private set; }
        public Task<UpdateDiscovery> DiscoverAsync(SourceSettings settings, IReadOnlyList<string> queries,
            bool needsBulkEligibility, CancellationToken cancellationToken, bool acceptSourceAgreements = false)
        {
            Queries = queries;
            AcceptedSourceAgreements = acceptSourceAgreements;
            return Task.FromResult(new UpdateDiscovery(Backend, packages, entries, warnings, Guid.NewGuid().ToString("N"), unavailable));
        }
    }
}
