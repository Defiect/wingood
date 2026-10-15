using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wingood.Core;

namespace Wingood.Tests;

[TestClass]
public sealed class BatchTests
{
    private static PackageCandidate Package(string id) => new(id, id, id, "1", "2", "user", "portable");

    [TestMethod]
    public async Task SubmitsBoundedRequestsAndRefillsWhenAnyCompletes()
    {
        var backend = new ControlledBackend();
        var packages = new[] { Package("A"), Package("B"), Package("C"), Package("D") };
        var task = new BatchRunner(backend).RunAsync(packages, 2, (_, _) => { }, _ => { }, CancellationToken.None);
        Assert.AreEqual("A", await backend.NextAsync());
        Assert.AreEqual("B", await backend.NextAsync());
        Assert.AreEqual(2, backend.StartedCount);

        // B completes before A; C must start without waiting for A.
        backend.Finish("B", new("InstallError", "0x8A150111", 6));
        Assert.AreEqual("C", await backend.NextAsync());
        Assert.IsFalse(task.IsCompleted);
        backend.Finish("C", new("Ok"));
        Assert.AreEqual("D", await backend.NextAsync());
        backend.Finish("D", new("Ok", RebootRequired: true));
        backend.Finish("A", new("Ok"));
        var results = await task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { "A", "B", "C", "D" }, results.Select(r => r.Package.Id).ToArray());
        Assert.AreEqual(3, results.Count(r => r.Result.Succeeded));
        Assert.AreEqual((uint)6, results[1].Result.InstallerErrorCode);
        Assert.IsTrue(results[3].Result.RebootRequired);
    }

    [TestMethod]
    public async Task MaximumInFlightSubmitsTheWholeBatchBeforeWaitingForCompletion()
    {
        var backend = new ControlledBackend();
        var packages = new[] { Package("A"), Package("B"), Package("C"), Package("D") };
        var task = new BatchRunner(backend).RunAsync(packages, int.MaxValue,
            (_, _) => { }, _ => { }, CancellationToken.None);

        foreach (var package in packages)
            Assert.AreEqual(package.Id, await backend.NextAsync());
        Assert.AreEqual(packages.Length, backend.StartedCount);
        Assert.IsFalse(task.IsCompleted);

        foreach (var package in packages)
            backend.Finish(package.Id, new UpgradeResult("Ok"));
        var results = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(packages.Length, results.Count);
        Assert.IsTrue(results.All(outcome => outcome.Result.Succeeded));
    }

    [TestMethod]
    public async Task CancellationStopsNewSubmissionsButDrainsRunningOperations()
    {
        var backend = new ControlledBackend();
        using var cancellation = new CancellationTokenSource();
        var task = new BatchRunner(backend).RunAsync([Package("A"), Package("B"), Package("C")], 2,
            (_, _) => { }, _ => { }, cancellation.Token);
        await backend.NextAsync();
        await backend.NextAsync();
        cancellation.Cancel();
        Assert.IsTrue(backend.Tokens["A"].IsCancellationRequested);
        Assert.IsFalse(task.IsCompleted);
        backend.Finish("A", new("Ok")); // Simulates an installer completing after Ctrl+C.
        backend.Cancel("B");
        var results = await task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { "Ok", "Cancelled", "NotStarted" }, results.Select(r => r.Result.Status).ToArray());
        Assert.AreEqual(2, backend.StartedCount);
    }

    [TestMethod]
    public async Task AlreadyCancelledBatchDoesNotCallBackend()
    {
        var backend = new ControlledBackend();
        var results = await new BatchRunner(backend).RunAsync([Package("A")], 4,
            (_, _) => { }, _ => { }, new CancellationToken(true));
        Assert.AreEqual(0, backend.StartedCount);
        Assert.AreEqual("NotStarted", results[0].Result.Status);
    }

    [TestMethod]
    public async Task BackendExceptionDoesNotDiscardOtherPackages()
    {
        var results = await new BatchRunner(new ThrowingBackend()).RunAsync([Package("A"), Package("B")], 2,
            (_, _) => { }, _ => { }, CancellationToken.None);
        Assert.AreEqual("Exception", results[0].Result.Status);
        Assert.AreEqual("Ok", results[1].Result.Status);
    }

    private sealed class ThrowingBackend : IUpgradeBackend
    {
        public Task<UpgradeResult> UpgradeAsync(PackageCandidate package, IProgress<UpgradeProgress> progress, CancellationToken cancellationToken) =>
            package.Id == "A" ? throw new InvalidOperationException("Fake activation failure") : Task.FromResult(new UpgradeResult("Ok"));
    }

    private sealed class ControlledBackend : IUpgradeBackend
    {
        private readonly Channel<string> started = Channel.CreateUnbounded<string>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<UpgradeResult>> tasks = new();
        public ConcurrentDictionary<string, CancellationToken> Tokens { get; } = new();
        public int StartedCount => tasks.Count;

        public Task<UpgradeResult> UpgradeAsync(PackageCandidate package, IProgress<UpgradeProgress> progress, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<UpgradeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tasks[package.Id] = completion;
            Tokens[package.Id] = cancellationToken;
            started.Writer.TryWrite(package.Id);
            return completion.Task;
        }

        public async Task<string> NextAsync() => await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void Finish(string id, UpgradeResult result) => tasks[id].SetResult(result);
        public void Cancel(string id) => tasks[id].SetCanceled();
    }
}
