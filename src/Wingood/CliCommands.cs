using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Wingood;

internal class SourceSettings : CommandSettings
{
    [CommandOption("--source <SOURCE>")]
    [Description("Limit to winget or store. msstore is also accepted.")]
    public string? Source { get; set; }

    [CommandOption("--scope <SCOPE>")]
    [Description("Limit installed copies to user or machine.")]
    public string? Scope { get; set; }

    [CommandOption("--color <MODE>")]
    [Description("Color mode: auto, always, or never. Honors NO_COLOR.")]
    [DefaultValue("auto")]
    public string Color { get; set; } = "auto";

    public override ValidationResult Validate()
    {
        if (Source is not null && Source.ToLowerInvariant() is not ("winget" or "store" or "msstore"))
            return ValidationResult.Error("--source must be winget or store.");
        if (Scope is not null && Scope.ToLowerInvariant() is not ("user" or "machine"))
            return ValidationResult.Error("--scope must be user or machine.");
        if (Color is not ("auto" or "always" or "never"))
            return ValidationResult.Error("--color must be auto, always, or never.");
        return ValidationResult.Success();
    }

    public string? NativeSource => Source?.ToLowerInvariant() switch { "store" => "msstore", var source => source };
}

internal sealed class UpgradeSettings : SourceSettings
{
    [CommandArgument(0, "[APP]")]
    [Description("One or more exact IDs or unambiguous app names. Omit for all eligible updates.")]
    public string[] Apps { get; set; } = [];

    [CommandOption("-y|--yes")]
    [Description("Proceed without confirmation and accept source/package agreements.")]
    public bool Yes { get; set; }

    [CommandOption("--all")]
    [Description("Proceed with the complete eligible selection without confirmation.")]
    public bool All { get; set; }

    [CommandOption("--dry-run")]
    [Description("Show the selection without downloading or installing.")]
    public bool DryRun { get; set; }

    [CommandOption("--exclude <APP>")]
    [Description("Exclude an app for this run. Repeat for multiple apps.")]
    public string[] Excludes { get; set; } = [];

    [CommandOption("--in-flight <COUNT>")]
    [Description("Advanced: limit outstanding operations. Default submits the entire batch.")]
    public int? InFlight { get; set; }

    public override ValidationResult Validate()
    {
        if (InFlight is < 1) return ValidationResult.Error("--in-flight must be at least 1.");
        if (All && Apps.Length > 0) return ValidationResult.Error("Use --all without app names, or name the apps to update.");
        return base.Validate();
    }
}

internal sealed class ShowSettings : SourceSettings
{
    [CommandArgument(0, "[APP]")]
    [Description("Show details for an app. Omit to list available updates.")]
    public string? App { get; set; }
}

internal sealed class UpgradeCommand : AsyncCommand<UpgradeSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, UpgradeSettings settings, CancellationToken cancellationToken)
        => new UpdateService(Program.Ui).UpgradeAsync(settings, retry: false, cancellationToken);
}

internal sealed class RetryCommand : AsyncCommand<UpgradeSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, UpgradeSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Apps.Length > 0 || settings.All) throw new ArgumentException("retry uses the last run's failed apps. Use upgrade to choose other apps.");
        return new UpdateService(Program.Ui).UpgradeAsync(settings, retry: true, cancellationToken);
    }
}

internal sealed class ShowCommand : AsyncCommand<ShowSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ShowSettings settings, CancellationToken cancellationToken)
    {
        if (settings.App is not null)
        {
            Program.Ui.Details(await NativeCli.DetailsAsync(settings.App, settings.NativeSource, cancellationToken));
            return 0;
        }
        return await new UpdateService(Program.Ui).ListAsync(settings, cancellationToken);
    }
}
