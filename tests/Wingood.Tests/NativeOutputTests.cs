using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wingood.Core;

namespace Wingood.Tests;

[TestClass]
public sealed class NativeOutputTests
{
    [TestMethod]
    public void ParsesOrdinaryRowsInNativeOrderAndExcludesExplicitOnlyTable()
    {
        var output = string.Join('\n',
            UpgradeTable(
                ["Name", "Id", "Version", "Available", "Source"],
                [
                    ["Air", "JetBrains.Air.Preview", "262.834 Public Preview", "262.834.70", "winget"],
                    ["NVIDIA Nsight Compute", "Nvidia.Nsight.Compute", "< 2026.3.1", "2026.3.1", "winget"],
                    ["Docker Desktop", "XP8CBJ40XLBWKX", "4.84.0", "4.94.0", "msstore"],
                ]),
            "",
            "46 upgrades available.",
            "The following packages have an upgrade available, but require explicit targeting for upgrade:",
            UpgradeTable(
                ["Name", "Id", "Version", "Available", "Source"],
                [["MSYS2", "MSYS2.MSYS2", "20260322", "20260611", "winget"]]),
            "3 package(s) have version numbers that cannot be determined.",
            "Use --include-unknown to see all results.");

        var upgrades = NativeOutput.ParseUpgrades(output);

        Assert.HasCount(3, upgrades);
        Assert.AreEqual("JetBrains.Air.Preview", upgrades[0].Id);
        Assert.AreEqual("262.834 Public Preview", upgrades[0].InstalledVersion);
        Assert.AreEqual("< 2026.3.1", upgrades[1].InstalledVersion);
        Assert.AreEqual("msstore", upgrades[2].Source);
        Assert.IsFalse(upgrades.Any(upgrade => upgrade.Id == "MSYS2.MSYS2"));
    }

    [TestMethod]
    public void OnlyExplicitOnlyTableIsAValidEmptyEligibilityResult()
    {
        var output = string.Join('\n',
            "The following packages have an upgrade available, but require explicit targeting for upgrade:",
            UpgradeTable(
                ["Name", "Id", "Version", "Available", "Source"],
                [["MSYS2", "MSYS2.MSYS2", "20260322", "20260611", "winget"]]),
            "1 upgrades available.");

        Assert.IsEmpty(NativeOutput.ParseUpgrades(output));
    }

    [TestMethod]
    public void RecognizesEmptySourceScopedResultAndSupportsSourceColumnOmission()
    {
        Assert.IsEmpty(NativeOutput.ParseUpgrades("No installed package found matching input criteria."));

        var output = UpgradeTable(
            ["Name", "Id", "Version", "Available"],
            [["Docker Desktop", "XP8CBJ40XLBWKX", "4.84.0", "4.94.0"]]);
        var parsed = NativeOutput.ParseUpgrades(output, "msstore");

        Assert.AreEqual("msstore", parsed.Single().Source);
    }

    [TestMethod]
    public void DuplicatePackageIdsFromDifferentSourcesRemainInNativeOrder()
    {
        var output = UpgradeTable(
            ["Name", "Id", "Version", "Available", "Source"],
            [
                ["Same App", "Vendor.App", "1.0", "2.0", "winget"],
                ["Same App", "Vendor.App", "1.0", "2.1", "msstore"],
            ]);

        var parsed = NativeOutput.ParseUpgrades(output);

        Assert.HasCount(2, parsed);
        Assert.AreEqual("winget", parsed[0].Source);
        Assert.AreEqual("msstore", parsed[1].Source);
    }

    [TestMethod]
    public void RejectsUnknownLayoutsTruncatedRowsAndCountOnlyOutput()
    {
        Assert.ThrowsExactly<FormatException>(() => NativeOutput.ParseUpgrades(""));
        Assert.ThrowsExactly<FormatException>(() => NativeOutput.ParseUpgrades("37 upgrades available."));
        Assert.ThrowsExactly<FormatException>(() => NativeOutput.ParseUpgrades("Package Id Version Available Source\n------------------------------"));
        Assert.ThrowsExactly<FormatException>(() => NativeOutput.ParseUpgrades(UpgradeTable(
            ["Name", "Id", "Version", "Available", "Source"],
            [["An App", "Vendor.App", "1.0", "2.0", ""]])));
    }

    [TestMethod]
    public void RejectsUnknownTextAndMissingTableSeparator()
    {
        Assert.ThrowsExactly<FormatException>(() => NativeOutput.ParseUpgrades("A future WinGet table format"));

        var noSeparator = "Name        Id          Version Available Source\nAn App      Vendor.App  1.0     2.0       winget";
        Assert.ThrowsExactly<FormatException>(() => NativeOutput.ParseUpgrades(noSeparator));
    }

    [TestMethod]
    public void ParsesDetailsWithContinuationTagsUnknownFieldsAndInstallerSection()
    {
        const string output = """
            Found Oh My Posh [JanDeDobbeleer.OhMyPosh]
            Version: 31.8.0
            Publisher: Jan De Dobbeleer
            Description: Prompt theme engine
              for any shell
            Tags:
              bash
              cli
            Future Field: kept as text
            Installer:
              Installer Type: msix
              Installer Locale: en-US
              Installer Url: https://example.test/app.msix
            Unattached future output
            """;

        var details = NativeOutput.ParseDetails(output);

        Assert.AreEqual("Oh My Posh", details.Name);
        Assert.AreEqual("JanDeDobbeleer.OhMyPosh", details.Id);
        var package = details.Sections.Single(section => section.Name == "Package");
        Assert.AreEqual("Prompt theme engine\nfor any shell", package.Fields.Single(field => field.Name == "Description").Value);
        Assert.AreEqual("bash\ncli", package.Fields.Single(field => field.Name == "Tags").Value);
        Assert.AreEqual("kept as text", package.Fields.Single(field => field.Name == "Future Field").Value);
        var installer = details.Sections.Single(section => section.Name == "Installer");
        Assert.AreEqual("msix", installer.Fields[0].Value);
        Assert.AreEqual("https://example.test/app.msix", installer.Fields[2].Value);
        CollectionAssert.AreEqual(new[] { "Unattached future output" }, details.RawLines.ToArray());
    }

    [TestMethod]
    public void DetailsRejectUnrecognizedOrTruncatedOutput()
    {
        Assert.ThrowsExactly<FormatException>(() => NativeOutput.ParseDetails("No package found matching input criteria."));
        Assert.ThrowsExactly<FormatException>(() => NativeOutput.ParseDetails("Found App [Vendor.App]"));
    }

    private static string UpgradeTable(string[] headers, string[][] rows)
    {
        var widths = headers.Select((header, index) => Math.Max(header.Length, rows.Length == 0 ? 0 : rows.Max(row => row[index].Length))).ToArray();
        string Format(string[] values) => string.Join(' ', values.Select((value, index) => value.PadRight(widths[index]))).TrimEnd();

        var header = Format(headers);
        var lines = new List<string> { header, new string('-', header.Length) };
        lines.AddRange(rows.Select(Format));
        return string.Join('\n', lines);
    }
}
