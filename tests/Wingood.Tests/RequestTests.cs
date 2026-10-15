using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wingood.Core;

namespace Wingood.Tests;

[TestClass]
public sealed class RequestTests
{
    [TestMethod]
    public void BareExecutablesUseTheirAgreedDefaults()
    {
        CollectionAssert.AreEqual(new[] { "upgrade" }, CliInvocation.Normalize([], "wingood"));
        CollectionAssert.AreEqual(new[] { "upgrade", "--yes" }, CliInvocation.Normalize([], "wg.exe"));
    }

    [TestMethod]
    public void FlagOnlyInvocationsGainUpgradeCommand()
    {
        CollectionAssert.AreEqual(new[] { "upgrade", "--dry-run" },
            CliInvocation.Normalize(["--dry-run"], "wingood.exe"));
        CollectionAssert.AreEqual(new[] { "upgrade", "--source", "store" },
            CliInvocation.Normalize(["--source", "store"], "wg"));
    }

    [TestMethod]
    public void HelpAndVersionRemainTopLevelAndNamedCommandsPassThrough()
    {
        foreach (var option in new[] { "--help", "-h", "--version" })
            CollectionAssert.AreEqual(new[] { option }, CliInvocation.Normalize([option], "wingood"));

        foreach (var alias in new[] { "update", "up" })
        {
            var named = new[] { alias, "--dry-run", "Example.App" };
            CollectionAssert.AreEqual(named, CliInvocation.Normalize(named, "wg.exe"));
        }
        CollectionAssert.AreEqual(new[] { "show", "Example.App" },
            CliInvocation.Normalize(["show", "Example.App"], "wingood"));
    }

    [TestMethod]
    public void DefaultYesRequiresInteractiveInputAndTreatsEnterAsYes()
    {
        Assert.IsFalse(Confirmation.Accepts(null, interactive: true));
        Assert.IsFalse(Confirmation.Accepts(null, interactive: false));
        Assert.IsFalse(Confirmation.Accepts("", interactive: false));
        Assert.IsTrue(Confirmation.Accepts("", interactive: true));
        Assert.IsTrue(Confirmation.Accepts("  ", interactive: true));
        Assert.IsTrue(Confirmation.Accepts("Y", interactive: true));
        Assert.IsTrue(Confirmation.Accepts(" yes ", interactive: true));
        Assert.IsFalse(Confirmation.Accepts("n", interactive: true));
        Assert.IsFalse(Confirmation.Accepts("maybe", interactive: true));
    }
}
