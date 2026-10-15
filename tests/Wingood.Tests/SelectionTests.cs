using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wingood.Core;

namespace Wingood.Tests;

[TestClass]
public sealed class SelectionTests
{
    private static readonly PackageCandidate UserCopy = new("a", "Vendor.App", "Vendor App", "1", "2", "user", "inno");

    [TestMethod]
    public void ExactIdThenExactNameThenUniqueSubstringMatchingPreservesOrderAndDeduplicatesByKey()
    {
        var editor = new PackageCandidate("b", "Tools.Editor", "Text Editor", "1", "2", "user", "portable");
        var selected = PackageSelection.Select([UserCopy, editor], ["App", "text editor", "Tools.Editor", "vendor.app"]);

        CollectionAssert.AreEqual(new[] { "a", "b" }, selected.Select(package => package.Key).ToArray());
    }

    [TestMethod]
    public void ExactIdTakesPrecedenceOverNameAndAmbiguousSubstringsListCandidates()
    {
        var exactId = new PackageCandidate("a", "Vendor", "Other App", "1", "2", "user", "inno");
        var matchingName = new PackageCandidate("b", "Other.Id", "Vendor", "1", "2", "user", "inno");
        Assert.AreEqual("a", PackageSelection.Select([exactId, matchingName], ["Vendor"])[0].Key);

        var anotherApp = new PackageCandidate("c", "Vendor.App2", "Another App", "1", "2", "user", "inno");
        var error = Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy, anotherApp], ["Vendor"]));
        StringAssert.Contains(error.Message, "Vendor");
        StringAssert.Contains(error.Message, "Vendor App");
        StringAssert.Contains(error.Message, "Another App");
    }

    [TestMethod]
    public void MissingEmptyAndOutOfScopeSelectionsFailClosed()
    {
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy], []));
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy], [" "]));
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy], ["Missing App"]));
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy], ["Vendor.App"], "machine"));
    }

    [TestMethod]
    public void ScopeDisambiguatesInstalledCopies()
    {
        var machine = UserCopy with { Key = "b", InstalledScope = "machine" };
        Assert.Throws<ArgumentException>(() => PackageSelection.Select([UserCopy, machine], ["Vendor.App"]));
        Assert.AreEqual("b", PackageSelection.Select([UserCopy, machine], ["Vendor.App"], "machine")[0].Key);
    }

    [TestMethod]
    public void ExclusionsResolveAgainstAllCandidatesBeforeFilteringAndCanNameUnselectedApps()
    {
        var selected = new[] { UserCopy };
        var explicitOnly = new PackageCandidate("b", "Vendor.Tool", "Tool", "1", "2", "user", "portable");
        var remaining = PackageSelection.Exclude(selected, [UserCopy, explicitOnly], ["Tool"]);
        CollectionAssert.AreEqual(new[] { "a" }, remaining.Select(package => package.Key).ToArray());

        Assert.Throws<ArgumentException>(() =>
            PackageSelection.Exclude(selected, [UserCopy, explicitOnly], ["Tool", "missing"]));
        CollectionAssert.AreEqual(new[] { "a" }, selected.Select(package => package.Key).ToArray());
    }

    [TestMethod]
    public void BulkBindingUsesExactIdSourceAndVersionsAndPreservesNativeOrder()
    {
        var winget = UserCopy with { Key = "winget", Source = "winget" };
        var store = UserCopy with { Key = "store", Source = "msstore", AvailableVersion = "3" };
        var editor = new PackageCandidate("editor", "Tools.Editor", "Text Editor", "4", "5", "user", "portable", "winget");
        var entries = new[]
        {
            new NativeUpgrade("Text Editor", editor.Id, "4", "5", "winget"),
            new NativeUpgrade("Vendor App", store.Id, "1", "3", "msstore"),
        };

        var bound = PackageSelection.BindBulk([winget, store, editor], entries);
        CollectionAssert.AreEqual(new[] { "editor", "store" }, bound.Select(package => package.Key).ToArray());
    }

    [TestMethod]
    public void BulkBindingRejectsUnknownDuplicateAndEmptyIds()
    {
        Assert.Throws<InvalidOperationException>(() => PackageSelection.BindBulk([UserCopy],
            [new NativeUpgrade("Vendor App", "Missing.App", "1", "2", "winget")]));
        Assert.Throws<InvalidOperationException>(() => PackageSelection.BindBulk([UserCopy, UserCopy with { Key = "copy" }],
            [new NativeUpgrade("Vendor App", "Vendor.App", "1", "2", "winget")]));
        Assert.Throws<InvalidOperationException>(() => PackageSelection.BindBulk([UserCopy],
            [new NativeUpgrade("Vendor App", "", "1", "2", "winget")]));
    }
}
