using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spectre.Console;
using Wingood.Core;

namespace Wingood.Cli.Tests;

[TestClass]
public sealed class TerminalTests
{
    [TestMethod]
    public void WideNarrowAndPlainPackageViewsPreserveLiteralMetadata()
    {
        foreach (var width in new[] { 50, 80, 140 })
        {
            var output = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(output), Interactive = InteractionSupport.No });
            console.Profile.Width = width;
            new Terminal(console, plain: false).Packages([new("a", "Example.App", "Example [red]app[/]", "1.0", "2.0", "user", "portable")]);
            var rendered = output.ToString();
            StringAssert.Contains(rendered, "[red]app[/]");
            StringAssert.Contains(rendered, "1.0");
            StringAssert.Contains(rendered, "2.0");
            Assert.IsFalse(rendered.Contains('\x1b'));
            foreach (var line in rendered.Split('\n')) Assert.IsTrue(line.TrimEnd('\r').Length <= width);
        }
    }

    [TestMethod]
    public void PlainDetailsKeepCompleteUrlsUnknownFieldsAndEscapeControlCodes()
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(output), Interactive = InteractionSupport.No });
        console.Profile.Width = 10000;
        var url = "https://example.com/" + new string('a', 120);
        new Terminal(console).Details(new("Literal [App]", "Example.App",
            [new("Package", [new("Version", "2"), new("Description", "Hello\x1b[31m [world]"), new("Custom field", "custom value"), new("Homepage", url)])], []));
        var rendered = output.ToString();
        StringAssert.Contains(rendered, "Literal [App]");
        StringAssert.Contains(rendered, url);
        StringAssert.Contains(rendered, "custom value");
        Assert.IsFalse(rendered.Contains('\x1b'));
    }
}
