using System.Diagnostics;
using System.Text;
using Wingood.Core;

namespace Wingood;

internal sealed record NativeCommandResult(int ExitCode, string Output, string Error);

internal static class NativeCli
{
    public static async Task<NativeCommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("winget")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start WinGet. Install or repair App Installer first.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public static async Task<IReadOnlyList<NativeUpgrade>> UpgradesAsync(string? source, string? scope, bool acceptSourceAgreements, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "upgrade", "--disable-interactivity" };
        if (acceptSourceAgreements) arguments.Add("--accept-source-agreements");
        if (source is not null) arguments.AddRange(["--source", source]);
        if (scope is not null) arguments.AddRange(["--scope", scope]);
        var result = await RunAsync(arguments, cancellationToken);
        // Parse even a nonzero result: WinGet uses a nonzero code for a recognized
        // empty search. Unfamiliar errors never become an update selection.
        try { return NativeOutput.ParseUpgrades(result.Output, source); }
        catch (FormatException error)
        {
            throw new InvalidOperationException($"WinGet's update list could not be read safely (0x{result.ExitCode:X8}). {error.Message} {result.Error.Trim()}", error);
        }
    }

    public static async Task<PackageDetails> DetailsAsync(string query, string? source, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "show", query, "--disable-interactivity" };
        if (source is not null) arguments.AddRange(["--source", source]);
        var result = await RunAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
            throw new ArgumentException($"WinGet could not select one package for '{query}'. Use its exact ID.\n{result.Output.Trim()}\n{result.Error.Trim()}");
        return NativeOutput.ParseDetails(result.Output);
    }
}
