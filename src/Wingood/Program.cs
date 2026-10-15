using Spectre.Console.Cli;
using Wingood.Core;

namespace Wingood;

internal static class Program
{
    internal static Terminal Ui { get; private set; } = new("auto");

    public static async Task<int> Main(string[] args)
    {
        var colorIndex = Array.IndexOf(args, "--color");
        var color = colorIndex >= 0 && colorIndex + 1 < args.Length ? args[colorIndex + 1] : "auto";
        color = args.FirstOrDefault(argument => argument.StartsWith("--color=", StringComparison.Ordinal))?[8..] ?? color;
        Ui = new Terminal(color);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, value) =>
        {
            value.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var app = new CommandApp();
            app.Configure(config =>
            {
                config.SetApplicationName(Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "wg" ? "wg" : "wingood");
                config.SetApplicationVersion("0.1.0");
                config.ConfigureConsole(Ui.Console);
                config.AddCommand<UpgradeCommand>("upgrade").WithAlias("update").WithAlias("up")
                    .WithDescription("Download ahead while WinGet installs app updates.");
                config.AddCommand<ShowCommand>("show").WithAlias("list")
                    .WithDescription("Show available updates or formatted package details.");
                config.AddCommand<RetryCommand>("retry").WithDescription("Retry failed apps from the last run.");
                config.SetExceptionHandler((error, _) =>
                {
                    if (error is OperationCanceledException) { Ui.Warn("Cancelled."); return 130; }
                    Ui.Error(error.Message);
                    if (error.HResult == unchecked((int)0x800706BA))
                        Ui.Warn("WinGet activation failed. Check the installed engine/SDK pairing and use a normal terminal. See the README.");
                    return error is ArgumentException or CommandParseException or CommandRuntimeException ? 2 : 1;
                });
            });
            var normalized = CliInvocation.Normalize(args, Environment.ProcessPath ?? "wingood");
            if (normalized[0] == "help") normalized = [.. normalized.Skip(1), "--help"];
            return await app.RunAsync(normalized, cancellation.Token);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
