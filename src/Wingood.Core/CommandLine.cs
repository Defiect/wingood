namespace Wingood.Core;

/// <summary>Normalizes the two executable entry points before Spectre parses arguments.</summary>
public static class CliInvocation
{
    public static string[] Normalize(string[] args, string executableName)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        var executable = Path.GetFileNameWithoutExtension(executableName);
        if (args.Length == 0)
            return executable.Equals("wg", StringComparison.OrdinalIgnoreCase)
                ? ["upgrade", "--yes"]
                : ["upgrade"];

        if (args[0] is "--help" or "-h" or "--version" or "-v")
            return args;

        // Spectre's command parser expects a command first. Bare entry points
        // accept upgrade flags directly, for example `wingood --source store`.
        return args[0].StartsWith("-", StringComparison.Ordinal)
            ? ["upgrade", .. args]
            : args;
    }
}

/// <summary>Implements the default-yes confirmation used by interactive upgrades.</summary>
public static class Confirmation
{
    public static bool Accepts(string? reply, bool interactive)
    {
        if (!interactive || reply is null)
            return false;

        var answer = reply.Trim();
        return answer.Length == 0
            || answer.Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
