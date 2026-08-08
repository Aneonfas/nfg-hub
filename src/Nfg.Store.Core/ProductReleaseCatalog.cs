using Nfg.Store.Contracts;

namespace Nfg.Store.Core;

/// <summary>
/// Normalizes and selects the releases exposed by a product manifest while
/// preserving compatibility with catalogs that only declare <see cref="ProductManifest.Release"/>.
/// </summary>
public static class ProductReleaseCatalog
{
    public const string SteamBuildPrefix = "steam-build-";

    /// <summary>
    /// Returns the legacy current release and all additional releases, de-duplicated
    /// by exact version text and ordered by descending SemVer precedence.
    /// </summary>
    public static IReadOnlyList<ProductRelease> GetAvailableReleases(ProductManifest product)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (product.Release is null)
        {
            throw new ArgumentException("The product must declare a current release.", nameof(product));
        }

        if (product.Releases is null)
        {
            throw new ArgumentException("The product release list cannot be null.", nameof(product));
        }

        var versions = new HashSet<string>(StringComparer.Ordinal);
        var releases = new List<ProductRelease>(product.Releases.Count + 1);

        AddRelease(product.Release);
        foreach (var release in product.Releases)
        {
            if (release is null)
            {
                throw new ArgumentException("The product release list cannot contain null entries.", nameof(product));
            }

            AddRelease(release);
        }

        return releases
            .OrderByDescending(release => release, ProductReleaseVersionComparer.Instance)
            .ToArray();

        void AddRelease(ProductRelease release)
        {
            if (!SemanticVersionComparer.IsValid(release.Version))
            {
                throw new ArgumentException(
                    $"Release version '{release.Version}' is not a valid Semantic Versioning 2.0.0 version.",
                    nameof(product));
            }

            if (versions.Add(release.Version))
            {
                releases.Add(release);
            }
        }
    }

    /// <summary>
    /// Gets the release-specific game version, falling back to the schema-v1
    /// product compatibility value.
    /// </summary>
    public static string? GetEffectiveGameVersion(
        ProductManifest product,
        ProductRelease release)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(product.Compatibility);

        return release.GameVersion ?? product.Compatibility.GameVersion;
    }

    /// <summary>
    /// Returns whether a release explicitly targets the detected Steam build id.
    /// The detected value is the numeric id reported by Steam, without a prefix.
    /// </summary>
    public static bool IsExactlyCompatible(
        ProductManifest product,
        ProductRelease release,
        string? detectedSteamBuildId)
    {
        if (!IsAsciiDigits(detectedSteamBuildId))
        {
            return false;
        }

        var expectedGameVersion = SteamBuildPrefix + detectedSteamBuildId;
        return string.Equals(
            GetEffectiveGameVersion(product, release),
            expectedGameVersion,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Selects the highest published release verified for the detected Steam build.
    /// When no exact match exists, returns the highest published release.
    /// </summary>
    public static ProductRelease? SelectRecommendedRelease(
        ProductManifest product,
        string? detectedSteamBuildId)
    {
        var publishedReleases = GetAvailableReleases(product)
            .Where(IsPublished)
            .ToArray();

        return publishedReleases.FirstOrDefault(release =>
                   IsExactlyCompatible(product, release, detectedSteamBuildId))
               ?? publishedReleases.FirstOrDefault();
    }

    /// <summary>
    /// Returns whether a release is downloadable and is not marked as a development release.
    /// </summary>
    public static bool IsPublished(ProductRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        return release.Payload is not null &&
               !release.Channel.Equals("development", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the manifest consumed by download, package validation, and installation
    /// services for one selected release.
    /// </summary>
    public static ProductManifest CreateManifestForRelease(
        ProductManifest product,
        ProductRelease release)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(product.Compatibility);

        return product with
        {
            Release = release,
            Compatibility = product.Compatibility with
            {
                GameVersion = GetEffectiveGameVersion(product, release)
            }
        };
    }

    private static bool IsAsciiDigits(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.All(character => character is >= '0' and <= '9');

    private sealed class ProductReleaseVersionComparer : IComparer<ProductRelease>
    {
        public static ProductReleaseVersionComparer Instance { get; } = new();

        public int Compare(ProductRelease? left, ProductRelease? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            if (!SemanticVersionComparer.TryCompare(left.Version, right.Version, out var comparison))
            {
                throw new InvalidOperationException("Available releases contain an invalid SemVer version.");
            }

            return comparison;
        }
    }
}
