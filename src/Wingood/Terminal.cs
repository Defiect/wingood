using System.Diagnostics;
using System.Text.RegularExpressions;
using Spectre.Console;
using Wingood.Core;

namespace Wingood;

internal sealed partial class Terminal
{
    public IAnsiConsole Console { get; }
    public bool Live => !System.Console.IsOutputRedirected && Console.Profile.Capabilities.Interactive;
    public bool Plain { get; }

    public Terminal(string color)
    {
        Plain = System.Console.IsOutputRedirected && color != "always";
        var enabled = color == "always" || color == "auto" && !System.Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null
            && Environment.GetEnvironmentVariable("TERM") != "dumb";
        Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = enabled ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = enabled ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Interactive = !System.Console.IsOutputRedirected ? InteractionSupport.Detect : InteractionSupport.No,
            // CI profile enrichers can otherwise re-enable ANSI even when the
            // caller explicitly requested plain output.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = enabled && color == "auto" },
        });
        if (Plain) Console.Profile.Width = 10000;
    }

    internal Terminal(IAnsiConsole console, bool plain = true) { Console = console; Plain = plain; }

    [GeneratedRegex(@"\x1b\][^\x07]*(?:\x07|\x1b\\)|\x1b\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex EscapeSequences();

    public static string Clean(string value) => new(EscapeSequences().Replace(value, "")
        .Where(character => !char.IsControl(character) || character == '\n').ToArray());
    public static string Text(string value) => Markup.Escape(Clean(value));
    public void Line(string text) => Console.MarkupLine(text);
    public void Warn(string message) => Line($"[yellow]{Text(message)}[/]");
    public void Error(string message) => Line($"[red]{Text(message)}[/]");

    public void Packages(IReadOnlyList<PackageCandidate> packages)
    {
        if (packages.Count == 0) { Line("[green]No eligible updates.[/]"); return; }
        if (Plain)
        {
            foreach (var package in packages)
            {
                Line($"[bold]{Text(package.Name)}[/]  [grey]{Text(package.Id)}[/] [cyan]({Text(package.Source)})[/]");
                Line($"  [grey]{Text(package.InstalledVersion)}[/] → [green]{Text(package.AvailableVersion)}[/]");
            }
            Line($"[bold]{packages.Count} update{(packages.Count == 1 ? "" : "s")}[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded).Expand();
        var narrow = Console.Profile.Width < 100;
        table.AddColumn("[bold]App[/]");
        if (narrow) table.AddColumn(new TableColumn("[green]Update[/]").Width(Math.Max(18, Console.Profile.Width / 3)));
        else
        {
            table.AddColumn(new TableColumn("[grey]Installed[/]").Width(Math.Min(23, packages.Max(package => package.InstalledVersion.Length))));
            table.AddColumn(new TableColumn("[green]Available[/]").Width(Math.Min(23, packages.Max(package => package.AvailableVersion.Length))));
            table.AddColumn(new TableColumn("[cyan]Source[/]").Width(7));
        }
        foreach (var package in packages)
        {
            var app = $"[bold]{Text(package.Name)}[/]\n[grey]{Text(package.Id)}[/]";
            if (narrow) app += $" [cyan]{Text(package.Source)}[/]";
            var cells = narrow ? new List<string> { app, $"[grey]{Text(package.InstalledVersion)}[/] → [green]{Text(package.AvailableVersion)}[/]" }
                : [app, $"[grey]{Text(package.InstalledVersion)}[/]", $"[green]{Text(package.AvailableVersion)}[/]", $"[cyan]{Text(package.Source)}[/]"];
            table.AddRow(cells.ToArray());
        }
        Console.Write(table);
        Line($"[bold]{packages.Count} update{(packages.Count == 1 ? "" : "s")}[/]");
    }

    public void Details(PackageDetails details)
    {
        var fields = details.Sections.SelectMany(section => section.Fields).ToArray();
        var version = fields.FirstOrDefault(field => field.Name.Equals("Version", StringComparison.OrdinalIgnoreCase))?.Value;
        Line($"[bold cyan]{Text(details.Name)}[/]{(version is null ? "" : $"  [green]{Text(version)}[/]")}");
        Line($"[grey]{Text(details.Id)}[/]");
        var description = fields.FirstOrDefault(field => field.Name.Equals("Description", StringComparison.OrdinalIgnoreCase))?.Value;
        if (description is not null) { Console.WriteLine(); Line(Text(description)); }
        foreach (var section in details.Sections)
        {
            var relevant = section.Fields.Where(field => field.Name is not ("Description" or "Version")).ToArray();
            var links = relevant.Where(field => field.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || field.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)).ToArray();
            var metadata = relevant.Except(links).Where(field => !field.Name.Equals("Tags", StringComparison.OrdinalIgnoreCase)).ToArray();
            var tags = relevant.FirstOrDefault(field => field.Name.Equals("Tags", StringComparison.OrdinalIgnoreCase));
            if (metadata.Length > 0) Fields(section.Name is "Package" or "General" ? "Details" : section.Name, metadata);
            if (links.Length > 0) Fields(section.Name == "Installer" ? "Installer links" : "Links", links, links: true);
            if (tags is not null)
            {
                Console.WriteLine();
                Line("[bold yellow]Tags[/]");
                Line($"[cyan]{Text(string.Join("  ·  ", tags.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))}[/]");
            }
        }
        if (details.RawLines.Count > 0)
        {
            Console.WriteLine(); Line("[bold yellow]Additional information[/]");
            foreach (var raw in details.RawLines) Line(Text(raw));
        }
    }

    private void Fields(string title, IReadOnlyList<PackageDetailField> fields, bool links = false)
    {
        Console.WriteLine();
        Line($"[bold yellow]{Text(title)}[/]");
        if (Plain)
        {
            foreach (var field in fields) Line($"[grey]{Text(FieldLabel(field.Name))}:[/] {(links ? $"[blue]{Text(field.Value)}[/]" : Text(field.Value))}");
            return;
        }
        var table = new Table().NoBorder().HideHeaders();
        table.AddColumn(new TableColumn("Field").Width(Math.Min(22, Math.Max(12, Console.Profile.Width / 4))));
        table.AddColumn("Value");
        foreach (var field in fields)
        {
            var value = Clean(field.Value);
            table.AddRow(new Markup($"[grey]{Text(FieldLabel(field.Name))}[/]"), links
                ? new Paragraph(value, new Style(foreground: Color.Blue, decoration: Decoration.Underline), new Link(value))
                : new Text(value));
        }
        Console.Write(table);
    }

    private static string FieldLabel(string name) => name switch
    {
        "Publisher Url" => "Publisher",
        "Publisher Support Url" => "Support",
        "Homepage" => "Website",
        "License Url" => "License",
        "Release Notes Url" => "Release notes",
        "Installer Url" => "Download",
        "Installer Type" => "Type",
        "Installer Locale" => "Locale",
        "Installer SHA256" => "SHA256",
        "Offline Distribution Supported" => "Offline distribution",
        _ => name,
    };

    public async Task<IReadOnlyList<PackageOutcome>> RunAsync(IUpgradeBackend backend,
        IReadOnlyList<PackageCandidate> packages, int inFlight, RunRecorder recorder, CancellationToken cancellationToken)
    {
        var gate = new object();
        var states = packages.ToDictionary(package => package.Key, _ => "Waiting");
        var ready = new HashSet<string>();
        var clock = Stopwatch.StartNew();

        void Progress(PackageCandidate package, UpgradeProgress progress)
        {
            recorder.Progress(package, progress);
            lock (gate)
            {
                if (progress.DownloadProgress >= 1 || progress.BytesRequired > 0 && progress.BytesDownloaded >= progress.BytesRequired)
                    ready.Add(package.Key);
                var state = progress.State switch
                {
                    "Downloading" when ready.Contains(package.Key) => "Ready to install",
                    "Downloading" => $"Downloading {Math.Clamp(progress.DownloadProgress, 0, 1):P0}",
                    "Installing" => "Installing",
                    "PostInstall" => "Finishing",
                    _ => "Waiting",
                };
                if (!Live && states[package.Key] != state) SafeLine($"[cyan]{Text(package.Name)}[/]  {Text(state)}");
                states[package.Key] = state;
            }
        }
        void Complete(PackageOutcome outcome)
        {
            recorder.Complete(outcome);
            lock (gate)
            {
                var status = outcome.Result.Succeeded ? "Done" : outcome.Result.Status is "Cancelled" or "NotStarted" ? "Cancelled" : "Failed";
                states[outcome.Package.Key] = status;
                if (!Live) SafeLine($"{Text(outcome.Package.Name)}  {StateText(status)}");
            }
        }
        Table Render()
        {
            lock (gate)
            {
                var done = states.Values.Count(state => state is "Done" or "Failed" or "Cancelled");
                var table = new Table().Border(TableBorder.Rounded).Expand()
                    .Title($"[bold]Updating · {done}/{packages.Count} finished · {clock.Elapsed.TotalSeconds:F0}s[/]");
                table.AddColumn("App"); table.AddColumn("Status");
                var visible = packages.OrderBy(package => states[package.Key] switch
                {
                    "Installing" => 0,
                    var state when state.StartsWith("Downloading", StringComparison.Ordinal) => 1,
                    "Finishing" => 2,
                    "Failed" => 3,
                    "Ready to install" => 4,
                    "Waiting" => 5,
                    _ => 6,
                }).Take(Math.Max(3, Console.Profile.Height - 9)).ToArray();
                foreach (var package in visible) table.AddRow(Text(package.Name), StateText(states[package.Key]));
                if (visible.Length < packages.Count) table.Caption($"[grey]{packages.Count - visible.Length} other apps in the batch[/]");
                return table;
            }
        }
        if (!Live) return await new BatchRunner(backend).RunAsync(packages, inFlight, Progress, Complete, cancellationToken);
        IReadOnlyList<PackageOutcome>? results = null;
        await Console.Live(Render()).AutoClear(false).StartAsync(async context =>
        {
            var run = new BatchRunner(backend).RunAsync(packages, inFlight, Progress, Complete, cancellationToken);
            while (!run.IsCompleted)
            {
                context.UpdateTarget(Render());
                await Task.WhenAny(run, Task.Delay(120));
            }
            results = await run;
            context.UpdateTarget(Render());
        });
        return results!;
    }

    private void SafeLine(string value)
    {
        try { Line(value); }
        catch (IOException) { /* Keep submitted operations running if an output pipe closes. */ }
    }
    private static string StateText(string state) => state switch
    {
        "Done" => "[green]Done[/]",
        "Failed" => "[red]Failed[/]",
        "Cancelled" => "[yellow]Cancelled[/]",
        "Ready to install" => "[cyan]Ready to install[/]",
        "Installing" => "[yellow]Installing[/]",
        _ when state.StartsWith("Downloading", StringComparison.Ordinal) => $"[blue]{Text(state)}[/]",
        _ => $"[grey]{Text(state)}[/]",
    };
}
