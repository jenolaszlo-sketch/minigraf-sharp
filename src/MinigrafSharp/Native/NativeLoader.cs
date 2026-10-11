using System.Reflection;
using System.Runtime.InteropServices;

namespace MinigrafSharp.Native;

/// <summary>
/// Loads the pinned minigraf-c library once per process and retains it for
/// process lifetime, so concurrent calls cannot race library unloading.
/// Resolution order: <c>MINIGRAFSHARP_NATIVE_PATH</c> override, then the
/// runtime-specific package location, then the repository staging layout.
/// No PATH probing, no network download.
/// </summary>
internal static class NativeLoader
{
    internal const string OverrideVariable = "MINIGRAFSHARP_NATIVE_PATH";
    internal const string LibraryName = "minigraf";

    internal const string PinnedMinigrafVersion = "2.0.5";
    internal const string PinnedMinigrafCommit = "a861e551162623c8f4752f94daf5b637543f8358";
    internal const string PinnedMinigrafCVersion = "2.0.5";
    internal const string PinnedMinigrafCCommit = "8422f9f048d8821a759cbe9e1c2ea6b8f53621dc";

    private static readonly object Sync = new();
    private static nint _handle;
    private static string? _loadedPath;
    private static bool _resolverRegistered;

    internal static string LoadedPath
    {
        get
        {
            EnsureLoaded();
            return _loadedPath!;
        }
    }

    internal static void EnsureLoaded()
    {
        lock (Sync)
        {
            RegisterResolverLocked();
            if (_handle != nint.Zero)
            {
                return;
            }

            string rid = GetRuntimeIdentifier();
            string fileName = GetNativeFileName(rid);

            string? overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                string resolved = ResolveOverridePath(overridePath, fileName);
                LoadFrom(resolved, rid, fileName, [$"{OverrideVariable}={resolved}"]);
                return;
            }

            string[] candidates = GetCandidatePaths(rid, fileName);
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    LoadFrom(candidate, rid, fileName, candidates);
                    return;
                }
            }

            throw LoadFailure(
                $"Minigraf native asset not found for RID '{rid}'.",
                rid,
                fileName,
                candidates);
        }
    }

    internal static string GetRuntimeIdentifier()
    {
        string? rid = SelectRuntimeIdentifier(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            RuntimeInformation.ProcessArchitecture);
        if (rid is not null)
        {
            return rid;
        }

        throw new PlatformNotSupportedException(
            $"Unsupported runtime: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}. " +
            $"Qualified RIDs: win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64. " +
            $"Pinned engine: minigraf-c {PinnedMinigrafCVersion}.");
    }

    // ProcessArchitecture describes this .NET process, which determines which
    // native asset it can load. Pure for unit tests.
    internal static string? SelectRuntimeIdentifier(
        bool isWindows,
        bool isLinux,
        bool isOSX,
        Architecture processArchitecture)
    {
        if (isWindows && processArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (isLinux && processArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (isLinux && processArchitecture == Architecture.Arm64)
        {
            return "linux-arm64";
        }

        if (isOSX && processArchitecture == Architecture.X64)
        {
            return "osx-x64";
        }

        if (isOSX && processArchitecture == Architecture.Arm64)
        {
            return "osx-arm64";
        }

        return null;
    }

    internal static string GetNativeFileName(string rid) =>
        rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? "minigraf.dll" :
        rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) ? "libminigraf.dylib" :
        "libminigraf.so";

    internal static string ResolveOverridePath(string overridePath, string fileName)
    {
        string full = Path.IsPathRooted(overridePath)
            ? overridePath
            : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, overridePath));
        if (Directory.Exists(full))
        {
            return Path.Combine(full, fileName);
        }

        return full;
    }

    internal static string[] GetCandidatePaths(string rid, string fileName)
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

    private static void LoadFrom(string path, string rid, string fileName, string[] probed)
    {
        if (!File.Exists(path))
        {
            throw LoadFailure(
                $"Minigraf native asset not found: {path}.",
                rid,
                fileName,
                probed);
        }

        nint handle;
        try
        {
            handle = NativeLibrary.Load(path);
        }
        catch (Exception ex)
        {
            throw LoadFailure(
                $"Failed to load the Minigraf native asset: {path}.",
                rid,
                fileName,
                probed,
                ex);
        }

        // Retained for process lifetime: concurrent calls cannot race unload.
        _handle = handle;
        _loadedPath = path;
    }

    private static MinigrafNativeLoadException LoadFailure(
        string reason,
        string rid,
        string fileName,
        string[] probed,
        Exception? inner = null)
    {
        string wrapperVersion =
            typeof(NativeLoader).Assembly.GetName().Version?.ToString() ?? "unknown";
        string message =
            $"{reason} RID {rid}, architecture {RuntimeInformation.ProcessArchitecture}, " +
            $"expected '{fileName}', probed [{string.Join("; ", probed)}]. " +
            $"MinigrafSharp {wrapperVersion}, pinned minigraf-c {PinnedMinigrafCVersion} " +
            $"(commit {PinnedMinigrafCCommit}) over Minigraf {PinnedMinigrafVersion}. " +
            $"Set {OverrideVariable} to a staged asset for local development.";
        return inner is null
            ? new MinigrafNativeLoadException(message)
            : new MinigrafNativeLoadException(message, inner);
    }

    private static void RegisterResolverLocked()
    {
        if (_resolverRegistered)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(
            typeof(NativeLoader).Assembly,
            static (libraryName, _, _) =>
            {
                if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase))
                {
                    return nint.Zero;
                }

                EnsureLoaded();
                return _handle;
            });
        _resolverRegistered = true;
    }
}
