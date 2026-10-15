namespace Wingood.Core;

public sealed record CommandLine(
    string Command, IReadOnlyList<string> Ids, string Source = "winget", string? Scope = null,
    int InFlight = 4, bool DryRun = false, bool AcceptAgreements = false,
    bool Json = false, string? Output = null)
{
    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0)
            return new("list", []);
        if (args is ["--help"] or ["-h"] or ["help"])
            return new("help", []);

        var command = args[0];
        if (command is not ("list" or "upgrade"))
            throw new ArgumentException($"Unknown command '{command}'. Use --help.");
        var ids = new List<string>();
        var source = "winget";
        string? scope = null;
        string? output = null;
        var inFlight = 4;
        var dryRun = false;
        var accept = false;
        var json = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source": source = Value(); break;
                case "--scope":
                    scope = Value().ToLowerInvariant();
                    if (scope is not ("user" or "machine"))
                        throw new ArgumentException("--scope must be user or machine.");
                    break;
                case "--in-flight":
                    if (!int.TryParse(Value(), out inFlight) || inFlight is < 1 or > 16)
                        throw new ArgumentException("--in-flight must be between 1 and 16.");
                    break;
                case "--output": output = Value(); break;
                case "--dry-run": dryRun = true; break;
                case "--accept-package-agreements": accept = true; break;
                case "--json": json = true; break;
                case "--all":
                    throw new ArgumentException("This version requires explicit package IDs: the API doesn't expose all of winget upgrade --all's exclusions. Run 'list' to choose a batch.");
                default:
                    if (args[i].StartsWith('-'))
                        throw new ArgumentException($"Unknown option '{args[i]}'. Use --help.");
                    ids.Add(args[i]);
                    break;
            }

            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith('-') || string.IsNullOrWhiteSpace(args[i]))
                    throw new ArgumentException($"Missing value for {args[i - 1]}.");
                return args[i];
            }
        }
        if (command == "upgrade" && ids.Count == 0)
            throw new ArgumentException("Specify one or more exact package IDs for upgrade.");
        if (command == "list" && (dryRun || accept || output is not null || inFlight != 4))
            throw new ArgumentException("--dry-run, --accept-package-agreements, --output and --in-flight apply to upgrade.");
        if (json && command == "upgrade" && !dryRun)
            throw new ArgumentException("--json applies to list or upgrade --dry-run. Upgrade runs always save JSON logs.");
        return new(command, ids, source, scope, inFlight, dryRun, accept, json, output);
    }
}
