using System.Reflection;
using System.Text.Json;

namespace MinigrafSharp;

/// <summary>
/// Read-only identity of the verified native implementation backing the
/// owned-shim surface (M3.2f; ADR 0003).
/// </summary>
/// <remarks>
/// Values come from the build-composed shim identity token
/// (<c>mgs_shim_identity</c>, verified byte-exact by the loader before any
/// handle is returned) plus the managed assembly's informational version.
/// The token is a declared compatibility/provenance identity, not a
/// cryptographic measurement of the loaded binary; exact binary hashes are
/// qualification evidence, not runtime API. Accessing <see cref="Current"/>
/// loads and verifies the shim (failing closed) and returns immutable values;
/// no mutable identity state is kept.
/// </remarks>
public sealed class MinigrafRuntimeIdentity
{
    private MinigrafRuntimeIdentity(
        string packageVersion,
        string shimAbiVersion,
        int shimSourceRevision,
        string coreVersion,
        string coreCommit)
    {
        PackageVersion = packageVersion;
        ShimAbiVersion = shimAbiVersion;
        ShimSourceRevision = shimSourceRevision;
        CoreVersion = coreVersion;
        CoreCommit = coreCommit;
    }

    /// <summary>
    /// Managed package version (the NuGet package version; semver build
    /// metadata such as the embedded source revision is not part of it).
    /// </summary>
    public string PackageVersion { get; }

    /// <summary>Shim ABI version declared by the verified token.</summary>
    public string ShimAbiVersion { get; }

    /// <summary>Shim source revision declared by the verified token.</summary>
    public int ShimSourceRevision { get; }

    /// <summary>Pinned Minigraf core version declared by the verified token.</summary>
    public string CoreVersion { get; }

    /// <summary>Pinned Minigraf core commit declared by the verified token.</summary>
    public string CoreCommit { get; }

    /// <summary>
    /// Returns the identity of the verified shim for this process. Loads and
    /// identity-verifies the shim first (the same gate used by every database
    /// open); a rejected candidate fails here with
    /// <see cref="MinigrafNativeLoadException"/> and is never cached, so a
    /// later valid resolution still succeeds.
    /// </summary>
    /// <exception cref="MinigrafNativeLoadException">The shim could not be
    /// loaded or its declared identity is not the pinned one.</exception>
    public static MinigrafRuntimeIdentity Current
    {
        get
        {
            // Positive verification happens in the loader before this returns
            // a handle; the pinned token is the exact bytes the loaded binary
            // declared (loader compares byte-exact).
            Native.ShimNativeLoader.EnsureLoadedAndVerified();
            return ParseAndValidate(Native.ShimNativeLoader.PinnedIdentityToken);
        }
    }

    /// <summary>
    /// Parses the closed identity token and requires exact agreement with the
    /// pinned compatibility/provenance constants. Internal so malformed and
    /// mismatched tokens can be qualified without a production seam; never
    /// called with caller-controlled input in production.
    /// </summary>
    internal static MinigrafRuntimeIdentity ParseAndValidate(string token)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(token);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new MinigrafNativeLoadException(
                "The shim identity token is malformed and cannot be trusted.", ex);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("the token root is not a JSON object");
        }

        int properties = 0;
        string? shimAbi = null;
        int? revision = null;
        string? coreVersion = null;
        string? coreCommit = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            properties++;
            switch (property.Name)
            {
                case "shim_abi":
                    shimAbi = ReadRequiredString(property, "shim_abi");
                    break;
                case "shim_source_revision":
                    revision = ReadRequiredRevision(property);
                    break;
                case "core_version":
                    coreVersion = ReadRequiredString(property, "core_version");
                    break;
                case "core_commit":
                    coreCommit = ReadRequiredString(property, "core_commit");
                    break;
                default:
                    throw Invalid("the token has an unexpected property '" + property.Name + "'");
            }
        }

        if (properties != 4 || shimAbi is null || revision is null ||
            coreVersion is null || coreCommit is null)
        {
            throw Invalid("the token must contain exactly shim_abi, shim_source_revision, core_version and core_commit");
        }

        if (!string.Equals(shimAbi, Native.ShimNativeLoader.PinnedShimAbiVersion, StringComparison.Ordinal))
        {
            throw Invalid($"the shim ABI version '{shimAbi}' is not the pinned '{Native.ShimNativeLoader.PinnedShimAbiVersion}'");
        }

        if (revision.Value != Native.ShimNativeLoader.PinnedShimSourceRevision)
        {
            throw Invalid($"the shim source revision '{revision.Value}' is not the pinned '{Native.ShimNativeLoader.PinnedShimSourceRevision}'");
        }

        if (!string.Equals(coreVersion, Native.ShimNativeLoader.PinnedCoreVersion, StringComparison.Ordinal))
        {
            throw Invalid($"the core version '{coreVersion}' is not the pinned '{Native.ShimNativeLoader.PinnedCoreVersion}'");
        }

        if (!string.Equals(coreCommit, Native.ShimNativeLoader.PinnedCoreCommit, StringComparison.Ordinal))
        {
            throw Invalid($"the core commit '{coreCommit}' is not the pinned '{Native.ShimNativeLoader.PinnedCoreCommit}'");
        }

        return new MinigrafRuntimeIdentity(
            ManagedPackageVersion(),
            shimAbi,
            revision.Value,
            coreVersion,
            coreCommit);
    }

    private static string ReadRequiredString(JsonProperty property, string name)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"token property '{name}' must be a string");
        }

        string value = property.Value.GetString() ?? string.Empty;
        return value.Length == 0
            ? throw Invalid($"token property '{name}' must not be empty")
            : value;
    }

    private static int ReadRequiredRevision(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number ||
            !property.Value.TryGetInt32(out int revision))
        {
            throw Invalid("token property 'shim_source_revision' must be an integer");
        }

        return revision;
    }

    private static string ManagedPackageVersion()
    {
        Assembly assembly = typeof(MinigrafRuntimeIdentity).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        // Semver build metadata (the embedded source revision) is not part of
        // the NuGet package version.
        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }

    private static MinigrafNativeLoadException Invalid(string reason) =>
        new("The shim identity token does not match the pinned identity: " + reason + ".");
}
