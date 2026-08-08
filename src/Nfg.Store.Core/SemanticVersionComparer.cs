namespace Nfg.Store.Core;

/// <summary>
/// Validates and compares Semantic Versioning 2.0.0 version strings.
/// </summary>
public static class SemanticVersionComparer
{
    /// <summary>
    /// Returns whether <paramref name="version"/> is a strict SemVer 2.0.0 version.
    /// </summary>
    public static bool IsValid(string? version) => TryParse(version, out _);

    /// <summary>
    /// Compares two strict SemVer 2.0.0 versions by precedence. Build metadata is ignored.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when both inputs are valid; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryCompare(
        string? leftVersion,
        string? rightVersion,
        out int comparison)
    {
        comparison = 0;
        if (!TryParse(leftVersion, out var left) ||
            !TryParse(rightVersion, out var right))
        {
            return false;
        }

        comparison = Compare(left, right);
        return true;
    }

    private static bool TryParse(string? value, out ParsedVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var buildSeparator = value.IndexOf('+');
        var precedencePart = buildSeparator < 0
            ? value
            : value[..buildSeparator];
        if (buildSeparator >= 0 &&
            !IsValidIdentifierList(value[(buildSeparator + 1)..], allowNumericLeadingZeros: true))
        {
            return false;
        }

        var prereleaseSeparator = precedencePart.IndexOf('-');
        var corePart = prereleaseSeparator < 0
            ? precedencePart
            : precedencePart[..prereleaseSeparator];
        var prereleasePart = prereleaseSeparator < 0
            ? null
            : precedencePart[(prereleaseSeparator + 1)..];

        var coreIdentifiers = corePart.Split('.');
        if (coreIdentifiers.Length != 3 ||
            coreIdentifiers.Any(identifier => !IsValidCoreIdentifier(identifier)) ||
            (prereleasePart is not null &&
             !IsValidIdentifierList(prereleasePart, allowNumericLeadingZeros: false)))
        {
            return false;
        }

        version = new ParsedVersion(
            coreIdentifiers[0],
            coreIdentifiers[1],
            coreIdentifiers[2],
            prereleasePart?.Split('.') ?? []);
        return true;
    }

    private static bool IsValidCoreIdentifier(string identifier) =>
        identifier.Length > 0 &&
        IsAsciiDigits(identifier) &&
        (identifier.Length == 1 || identifier[0] != '0');

    private static bool IsValidIdentifierList(
        string value,
        bool allowNumericLeadingZeros)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || identifier.Any(character => !IsIdentifierCharacter(character)))
            {
                return false;
            }

            if (!allowNumericLeadingZeros &&
                identifier.Length > 1 &&
                identifier[0] == '0' &&
                IsAsciiDigits(identifier))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierCharacter(char character) =>
        character is >= '0' and <= '9' or
            >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            '-';

    private static bool IsAsciiDigits(string value) =>
        value.All(character => character is >= '0' and <= '9');

    private static int Compare(ParsedVersion left, ParsedVersion right)
    {
        var comparison = CompareNumericIdentifier(left.Major, right.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareNumericIdentifier(left.Minor, right.Minor);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareNumericIdentifier(left.Patch, right.Patch);
        return comparison != 0
            ? comparison
            : ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    private static int ComparePrerelease(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            if (left.Count == right.Count)
            {
                return 0;
            }

            return left.Count == 0 ? 1 : -1;
        }

        var sharedCount = Math.Min(left.Count, right.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            var leftIdentifier = left[index];
            var rightIdentifier = right[index];
            var leftIsNumeric = IsAsciiDigits(leftIdentifier);
            var rightIsNumeric = IsAsciiDigits(rightIdentifier);

            int comparison;
            if (leftIsNumeric && rightIsNumeric)
            {
                comparison = CompareNumericIdentifier(leftIdentifier, rightIdentifier);
            }
            else if (leftIsNumeric != rightIsNumeric)
            {
                comparison = leftIsNumeric ? -1 : 1;
            }
            else
            {
                comparison = Math.Sign(string.CompareOrdinal(leftIdentifier, rightIdentifier));
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count == right.Count
            ? 0
            : left.Count < right.Count
                ? -1
                : 1;
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return left.Length < right.Length ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    private readonly record struct ParsedVersion(
        string Major,
        string Minor,
        string Patch,
        IReadOnlyList<string> Prerelease);
}
