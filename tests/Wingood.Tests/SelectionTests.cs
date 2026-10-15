using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wingood.Core;

namespace Wingood.Tests;

[TestClass]
public sealed class SelectionTests
{
    private static readonly PackageCandidate UserCopy = new("a", "Vendor.App", "App", "1", "2", "user", "inno");

    [TestMethod]
    public void SelectionIsExactCaseInsensitiveAndDeduplicated()
    {
        var selected = PackageSelection.Select([UserCopy], ["vendor.app", "Vendor.App"]);
        Assert.HasCount(1, selected);
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy], ["Vendor"]));
    }

    [TestMethod]
    public void MissingMemberRejectsTheWholeBatch()
    {
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy], ["Vendor.App", "Missing.App"]));
    }

    [TestMethod]
    public void DuplicateInstalledCopiesNeedUnambiguousScope()
    {
        var machine = UserCopy with { Key = "b", InstalledScope = "machine" };
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy, machine], ["Vendor.App"]));
        Assert.AreEqual("b", PackageSelection.Select([UserCopy, machine], ["Vendor.App"], "machine")[0].Key);
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy, UserCopy with { Key = "c" }], ["Vendor.App"], "user"));
    }

    [TestMethod]
    public void DefaultCommandIsReadOnlyAndUpgradeNeedsIds()
    {
        Assert.AreEqual("list", CommandLine.Parse([]).Command);
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["upgrade"]));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["upgrade", "--all"]));
    }

    [TestMethod]
    public void InvalidFlagsAndValuesFailClosed()
    {
        foreach (var args in new[]
        {
            new[] { "upgrade", "Vendor.App", "--in-flight", "0" },
            ["upgrade", "Vendor.App", "--in-flight", "17"],
            ["upgrade", "Vendor.App", "--source"],
            ["upgrade", "Vendor.App", "--scope", "other"],
            ["upgrade", "Vendor.App", "--json"],
            ["upgrade", "Vendor.App", "--force"],
        }) Assert.Throws<ArgumentException>(() => CommandLine.Parse(args));
    }

    [TestMethod]
    public void DryRunPreservesExplicitSelectionAndDoesNotAcceptAgreementsByDefault()
    {
        var options = CommandLine.Parse(["upgrade", "Vendor.App", "--dry-run", "--json", "--in-flight", "3"]);
        Assert.IsTrue(options.DryRun);
        Assert.IsTrue(options.Json);
        Assert.IsFalse(options.AcceptAgreements);
        Assert.AreEqual(3, options.InFlight);
        CollectionAssert.AreEqual(new[] { "Vendor.App" }, options.Ids.ToArray());
    }
}
