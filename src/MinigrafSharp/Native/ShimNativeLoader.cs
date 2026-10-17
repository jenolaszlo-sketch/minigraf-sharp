using System.Reflection;
using System.Runtime.InteropServices;

namespace MinigrafSharp.Native;

/// <summary>
/// Loads the owned prepared-query shim (M3.1a asset) once per process for
/// process lifetime and verifies its identity positively before any managed
/// call can reach it. Resolution order: <c>MINIGRAFSHARP_SHIM_NATIVE_PATH</c>
/// override, then the runtime-specific package location, then the repository
/// staging layout. No PATH probing, no network download, no substitution by
/// or of the official library; a failed load never caches a handle.
/// </summary>
internal static class ShimNativeLoader
{
    internal const string OverrideVariable = "MINIGRAFSHARP_SHIM_NATIVE_PATH";
    internal const string LibraryName = "minigrafsharp_shim";

    // Pinned provenance, mirrored from native/upstream.json (shim block) and
    // native/shim/identity.json. The identity check compares the loaded
    // binary's declared identity token byte-exact against these constants.
    // The token is a declared compatibility/provenance identity, not a
    // binary-content hash (M3.1a evidence records that distinction).
    internal const string PinnedShimAbiVersion = "0.1.0";
    internal const int PinnedShimSourceRevision = 1;
    internal const string PinnedCoreVersion = "2.0.7";
    internal const string PinnedCoreCommit = "2d2e5d75f28e91d61be9f293de895e2271e62a29";

    internal static readonly string PinnedIdentityToken =
        $"{{\"shim_abi\":\"{PinnedShimAbiVersion}\"," +
        $"\"shim_source_revision\":{PinnedShimSourceRevision}," +
        $"\"core_version\":\"{PinnedCoreVersion}\"," +
        $"\"core_commit\":\"{PinnedCoreCommit}\"}}";

    private static readonly object Sync = new();
    private static nint _handle;
    private static string? _loadedPath;

    internal static string LoadedPath
    {
        get
        {
            EnsureLoadedAndVerified();
            return _loadedPath!;
        }
    }

    /// <summary>
    /// Loads and identity-verifies the shim, returning its handle. Called
    /// only from the combined assembly resolver. Fails closed: symbol
    /// presence alone is not identity (ADR 0002 hard invariant 6).
    /// </summary>
    internal static nint EnsureLoadedAndVerified()
    {
        lock (Sync)
        {
            // Ensure the assembly-level resolver routes the shim's P/Invoke
            // name to this loader. On POSIX, dlopen of an already-loaded
            // module by short name does not resolve to it, so a shim-only
            // path (no official engine use first) must register the
            // resolver itself. Registration is idempotent and routes
            // strictly by library name.
            NativeLoader.RegisterResolverLocked();

            if (_handle != nint.Zero)
            {
                return _handle;
            }

            string rid = NativeLoader.GetRuntimeIdentifier();
            string fileName = GetNativeFileName(rid);

            string? overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
            string candidate;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                candidate = ResolveOverridePath(overridePath, fileName);
            }
            else
            {
                string[] candidates = GetCandidatePaths(rid, fileName);
                string? found = candidates.FirstOrDefault(File.Exists);
                if (found is null)
                {
                    throw LoadFailure(
                        $"Minigraf shim asset not found for RID '{rid}'.",
                        rid,
                        fileName,
                        candidates);
                }

                candidate = found;
            }

            if (!File.Exists(candidate))
            {
                throw LoadFailure(
                    $"Minigraf shim asset not found: {candidate}.",
                    rid,
                    fileName,
                    [candidate]);
            }

            nint handle;
            try
            {
                handle = NativeLibrary.Load(candidate);
            }
            catch (Exception ex)
            {
                throw LoadFailure(
                    $"Failed to load the Minigraf shim asset: {candidate}.",
                    rid,
                    fileName,
                    [candidate],
                    ex);
            }

            // The identity export is read directly (no P/Invoke and no
            // resolver recursion). The token is borrowed static state:
            // copied immediately, never freed. Verification failure
            // unloads the rejected candidate and leaves nothing cached,
            // so a corrected override resolves afresh.
            try
            {
                if (!NativeLibrary.TryGetExport(handle, "mgs_shim_identity", out nint fn)
                    || fn == nint.Zero)
                {
                    NativeLibrary.Free(handle);
                    throw LoadFailure(
                        "The loaded asset exposes no mgs_shim_identity export; " +
                        "it is not the owned MinigrafSharp shim.",
                        rid,
                        fileName,
                        [candidate]);
                }

                var identityFn =
                    Marshal.GetDelegateForFunctionPointer<NativeExports.IdentityFn>(fn);
                nint tokenPtr = identityFn();
                string? token = Interop.NativeUtf8.ReadCString(tokenPtr);
                if (token is null || !string.Equals(token, PinnedIdentityToken, StringComparison.Ordinal))
                {
                    NativeLibrary.Free(handle);
                    throw LoadFailure(
                        $"The loaded shim identity token {(token is null ? "could not be read" : $"'{token}'")} " +
                        $"does not match the pinned identity '{PinnedIdentityToken}'.",
                        rid,
                        fileName,
                        [candidate]);
                }
            }
            catch (MinigrafNativeLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                NativeLibrary.Free(handle);
                throw LoadFailure(
                    $"The loaded asset failed the shim identity check: {candidate}.",
                    rid,
                    fileName,
                    [candidate],
                    ex);
            }

            _handle = handle;
            _loadedPath = candidate;
            return handle;
        }
    }

    internal static string GetNativeFileName(string rid) =>
        rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? "minigrafsharp_shim.dll" :
        rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) ? "libminigrafsharp_shim.dylib" :
        "libminigrafsharp_shim.so";

    private static string ResolveOverridePath(string overridePath, string fileName)
    {
        string full = Path.IsPathRooted(overridePath)
            ? overridePath
            : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, overridePath));
        return Directory.Exists(full)
            ? Path.Combine(full, fileName)
            : full;
    }

    private static string[] GetCandidatePaths(string rid, string fileName)
    {
        string baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "runtimes", rid, "native", fileName),
            Path.Combine(baseDir, fileName),
        };

        string? staging = FindStagingPath(rid, fileName);
        if (staging is not null)
        {
            candidates.Add(staging);
        }

        return [.. candidates];
    }

    private static string? FindStagingPath(string rid, string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "native", "staging", rid, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return null;
    }

    private static MinigrafNativeLoadException LoadFailure(
        string reason,
        string rid,
        string fileName,
        string[] probed,
        Exception? inner = null)
    {
        string wrapperVersion =
            typeof(ShimNativeLoader).Assembly.GetName().Version?.ToString() ?? "unknown";
        string message =
            $"{reason} RID {rid}, architecture {RuntimeInformation.ProcessArchitecture}, " +
            $"expected '{fileName}', probed [{string.Join("; ", probed)}]. " +
            $"MinigrafSharp {wrapperVersion}, pinned shim ABI {PinnedShimAbiVersion} " +
            $"(source revision {PinnedShimSourceRevision}) over Minigraf {PinnedCoreVersion} " +
            $"(commit {PinnedCoreCommit}). Set {OverrideVariable} to the staged owned shim " +
            "asset for local development.";
        return inner is null
            ? new MinigrafNativeLoadException(message)
            : new MinigrafNativeLoadException(message, inner);
    }

    /// <summary>Function-pointer signature of <c>mgs_shim_identity</c>.</summary>
    private static class NativeExports
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate nint IdentityFn();
    }
}
