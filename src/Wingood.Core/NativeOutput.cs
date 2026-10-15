using System.Text.RegularExpressions;

namespace Wingood.Core;

/// <summary>
/// Parsed row from WinGet's ordinary (bulk-upgrade eligible) upgrade table.
/// </summary>
public sealed record NativeUpgrade(
    string Name,
    string Id,
    string InstalledVersion,
    string AvailableVersion,
    string Source);

/// <summary>A key/value pair from a WinGet package-details section.</summary>
public sealed record PackageDetailField(string Name, string Value);

/// <summary>An ordered group of package-details fields.</summary>
public sealed record PackageDetailSection(string Name, IReadOnlyList<PackageDetailField> Fields);

/// <summary>
/// Structured package details. Unknown lines that cannot be attached to a field
/// are retained verbatim in <see cref="RawLines"/>.
/// </summary>
public sealed record PackageDetails(
    string Name,
    string Id,
    IReadOnlyList<PackageDetailSection> Sections,
    IReadOnlyList<string> RawLines);

/// <summary>
/// Narrow parser for the human-readable WinGet output used for read-only
/// eligibility discovery and package details.
/// </summary>
public static partial class NativeOutput
{
    private const string ExplicitOnlyHeading = "The following packages have an upgrade available, but require explicit targeting for upgrade:";

    private static readonly Regex HeaderTokenPattern = new(@"\S+", RegexOptions.CultureInvariant);
    private static readonly Regex UpgradeTotalPattern = new(@"^\d+ upgrades? available\.$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex UnknownVersionCountPattern = new(@"^\d+ package\(s\) have version numbers that cannot be determined\.(?: Use --include-unknown to see all results\.)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DetailHeaderPattern = new(@"^Found (?<name>.+?) \[(?<id>[^\]]+)\]$", RegexOptions.CultureInvariant);
    private static readonly Regex DetailFieldPattern = new(@"^(?<name>[^:]+):(?:\s*(?<value>.*))?$", RegexOptions.CultureInvariant);
    private static readonly Regex PackageIdPattern = new(@"^\S+$", RegexOptions.CultureInvariant);
    private static readonly Regex SourcePattern = new(@"^\S+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the ordinary eligibility table from read-only <c>winget upgrade</c>
    /// output. Rows in the later explicit-targeting table are validated but
    /// excluded. When WinGet omits the Source column for a source-scoped query,
    /// <paramref name="sourceOverride"/> supplies the known source identity.
    /// </summary>
    /// <exception cref="FormatException">The output is empty, truncated, or has an unfamiliar table layout.</exception>
    public static IReadOnlyList<NativeUpgrade> ParseUpgrades(string output, string? sourceOverride = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        var lines = NormalizeLines(output);
        var eligible = new List<NativeUpgrade>();
        var ordinaryTableSeen = false;
        var explicitTableSeen = false;
        var explicitHeadingSeen = false;
        var recognizedEmptyResult = false;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (IsExplicitOnlyHeading(trimmed))
            {
                if (explicitHeadingSeen || explicitTableSeen)
                {
                    throw LayoutError("duplicate explicit-targeting table heading");
                }

                explicitHeadingSeen = true;
                continue;
            }

            if (TryReadUpgradeHeader(line, out var columns))
            {
                if (lineIndex + 1 >= lines.Length || !IsSeparator(lines[lineIndex + 1], line.Length))
                {
                    throw LayoutError("upgrade table is missing its separator row");
                }

                var isExplicitOnly = explicitHeadingSeen;
                if (isExplicitOnly)
                {
                    explicitHeadingSeen = false;
                    if (explicitTableSeen)
                    {
                        throw LayoutError("more than one explicit-targeting table was found");
                    }

                    explicitTableSeen = true;
                }
                else
                {
                    if (ordinaryTableSeen)
                    {
                        throw LayoutError("more than one ordinary upgrade table was found");
                    }

                    ordinaryTableSeen = true;
                }

                var rowIndex = lineIndex + 2;
                while (rowIndex < lines.Length)
                {
                    var row = lines[rowIndex];
                    var rowTrimmed = row.Trim();
                    if (rowTrimmed.Length == 0)
                    {
                        rowIndex++;
                        break;
                    }

                    if (IsExplicitOnlyHeading(rowTrimmed) || TryReadUpgradeHeader(row, out _))
                    {
                        break;
                    }

                    if (IsUpgradeFooter(rowTrimmed) || IsRecognizedEmptyResult(rowTrimmed))
                    {
                        break;
                    }

                    var parsed = ParseUpgradeRow(row, columns, sourceOverride);
                    if (!isExplicitOnly)
                    {
                        eligible.Add(parsed);
                    }

                    rowIndex++;
                }

                lineIndex = rowIndex - 1;
                continue;
            }

            if (IsUpgradeFooter(trimmed))
            {
                continue;
            }

            if (IsRecognizedEmptyResult(trimmed))
            {
                recognizedEmptyResult = true;
                continue;
            }

            throw LayoutError($"unrecognized line outside an upgrade table: '{trimmed}'");
        }

        if (explicitHeadingSeen)
        {
            throw LayoutError("explicit-targeting heading has no table");
        }

        if (!ordinaryTableSeen && !explicitTableSeen && !recognizedEmptyResult)
        {
            throw LayoutError("no recognized upgrade table or empty-result message was found");
        }

        return eligible.AsReadOnly();
    }

    /// <summary>
    /// Parses the text output from <c>winget show</c>, retaining field order,
    /// repeated fields, continuation lines, nested installer fields, and
    /// otherwise-unattached lines.
    /// </summary>
    /// <exception cref="FormatException">The output lacks a recognized package header or version field.</exception>
    public static PackageDetails ParseDetails(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var lines = NormalizeLines(output);
        var headerIndex = Array.FindIndex(lines, line => DetailHeaderPattern.IsMatch(line.Trim()));
        if (headerIndex < 0)
        {
            throw new FormatException("Unrecognized WinGet details output: package header was not found.");
        }

        var headerMatch = DetailHeaderPattern.Match(lines[headerIndex].Trim());
        var name = headerMatch.Groups["name"].Value.Trim();
        var id = headerMatch.Groups["id"].Value.Trim();
        if (name.Length == 0 || !PackageIdPattern.IsMatch(id))
        {
            throw new FormatException("Unrecognized WinGet details output: package name or ID is invalid.");
        }

        var packageFields = new List<MutableDetailField>();
        var installerFields = new List<MutableDetailField>();
        var rawLines = lines.Take(headerIndex).Where(line => line.Length > 0).ToList();
        var activeField = (MutableDetailField?)null;
        var inInstaller = false;

        for (var index = headerIndex + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            var indentation = line.Length - line.TrimStart().Length;
            var content = line.Trim();
            var fieldMatch = DetailFieldPattern.Match(content);

            if (indentation == 0 && fieldMatch.Success)
            {
                var fieldName = fieldMatch.Groups["name"].Value.Trim();
                var value = fieldMatch.Groups["value"].Success ? fieldMatch.Groups["value"].Value : string.Empty;
                if (fieldName.Equals("Installer", StringComparison.OrdinalIgnoreCase) && value.Length == 0)
                {
                    inInstaller = true;
                    activeField = null;
                }
                else
                {
                    inInstaller = false;
                    activeField = new MutableDetailField(fieldName, value);
                    packageFields.Add(activeField);
                }

                continue;
            }

            if (inInstaller && indentation > 0 && fieldMatch.Success)
            {
                activeField = new MutableDetailField(fieldMatch.Groups["name"].Value.Trim(),
                    fieldMatch.Groups["value"].Success ? fieldMatch.Groups["value"].Value : string.Empty);
                installerFields.Add(activeField);
                continue;
            }

            if (indentation > 0 && activeField is not null)
            {
                activeField.Append(content);
            }
            else
            {
                rawLines.Add(line);
            }
        }

        if (!packageFields.Any(field => field.Name.Equals("Version", StringComparison.OrdinalIgnoreCase) && field.Value.Length > 0))
        {
            throw new FormatException("Unrecognized WinGet details output: package version field was not found.");
        }

        var sections = new List<PackageDetailSection>
        {
            new("Package", packageFields.Select(field => field.ToField()).ToArray()),
        };
        if (installerFields.Count > 0 || lines.Skip(headerIndex + 1).Any(line => line.Trim().Equals("Installer:", StringComparison.OrdinalIgnoreCase)))
        {
            sections.Add(new PackageDetailSection("Installer", installerFields.Select(field => field.ToField()).ToArray()));
        }

        return new PackageDetails(name, id, sections.AsReadOnly(), rawLines.AsReadOnly());
    }

    private static string[] NormalizeLines(string output)
    {
        // WinGet is normally colorless when redirected. Removing ANSI styling
        // sequences also keeps a configured terminal color mode from shifting
        // fixed-column boundaries.
        return AnsiEscapePattern().Replace(output, string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static bool TryReadUpgradeHeader(string line, out UpgradeColumns columns)
    {
        var tokens = HeaderTokenPattern.Matches(line);
        var names = tokens.Select(token => token.Value).ToArray();
        var hasSource = names.SequenceEqual(["Name", "Id", "Version", "Available", "Source"], StringComparer.Ordinal);
        var hasNoSource = names.SequenceEqual(["Name", "Id", "Version", "Available"], StringComparer.Ordinal);
        if (!hasSource && !hasNoSource)
        {
            columns = new UpgradeColumns([], false);
            return false;
        }

        if (tokens[0].Index != 0)
        {
            columns = new UpgradeColumns([], false);
            return false;
        }

        columns = new UpgradeColumns(tokens.Select(token => token.Index).ToArray(), hasSource);
        return true;
    }

    private static bool IsSeparator(string line, int headerLength)
    {
        var separator = line.Trim();
        return separator.Length >= headerLength && separator.All(character => character == '-');
    }

    private static NativeUpgrade ParseUpgradeRow(string line, UpgradeColumns columns, string? sourceOverride)
    {
        var values = new string[columns.Starts.Length];
        for (var index = 0; index < columns.Starts.Length; index++)
        {
            var start = columns.Starts[index];
            var end = index + 1 < columns.Starts.Length ? columns.Starts[index + 1] : line.Length;
            values[index] = start >= line.Length ? string.Empty : line[start..Math.Min(end, line.Length)].Trim();
        }

        var source = columns.HasSource ? values[4] : sourceOverride?.Trim() ?? string.Empty;
        if (values[0].Length == 0 || !PackageIdPattern.IsMatch(values[1]) || values[2].Length == 0 || values[3].Length == 0 || !SourcePattern.IsMatch(source))
        {
            throw LayoutError($"malformed or truncated upgrade row: '{line.Trim()}'");
        }

        return new NativeUpgrade(values[0], values[1], values[2], values[3], source);
    }

    private static bool IsExplicitOnlyHeading(string line) => line.Equals(ExplicitOnlyHeading, StringComparison.OrdinalIgnoreCase);

    private static bool IsUpgradeFooter(string line) =>
        UpgradeTotalPattern.IsMatch(line)
        || UnknownVersionCountPattern.IsMatch(line)
        || line.Equals("Use --include-unknown to see all results.", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecognizedEmptyResult(string line) =>
        line.Equals("No installed package found matching input criteria.", StringComparison.OrdinalIgnoreCase)
        || line.Equals("No applicable upgrade found.", StringComparison.OrdinalIgnoreCase)
        || line.Equals("No applicable upgrade found", StringComparison.OrdinalIgnoreCase)
        || line.Equals("No package found matching input criteria.", StringComparison.OrdinalIgnoreCase);

    private static FormatException LayoutError(string detail) =>
        new($"Unrecognized or incomplete WinGet upgrade output: {detail}.");

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();

    private sealed record UpgradeColumns(int[] Starts, bool HasSource);

    private sealed class MutableDetailField(string name, string value)
    {
        private readonly List<string> _continuations = [];

        public string Name { get; } = name;
        public string Value
        {
            get
            {
                var parts = new List<string>();
                if (value.Length > 0)
                {
                    parts.Add(value);
                }

                parts.AddRange(_continuations);
                return string.Join('\n', parts);
            }
        }

        public void Append(string line) => _continuations.Add(line);

        public PackageDetailField ToField() => new(Name, Value);
    }
}
