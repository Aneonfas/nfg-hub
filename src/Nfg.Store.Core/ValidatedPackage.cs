using Nfg.Store.Contracts;

namespace Nfg.Store.Core;

public sealed record ValidatedPackage(
    string ArchivePath,
    string WrapperDirectory,
    NfgPackageManifest Manifest,
    IReadOnlyList<ValidatedPackageFile> Files);

public sealed record ValidatedPackageFile(
    string ArchiveEntryPath,
    string Destination,
    string DestinationRelativePath,
    long SizeBytes,
    string Sha256);
