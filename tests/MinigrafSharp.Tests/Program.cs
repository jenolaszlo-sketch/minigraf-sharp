// M0 unit tests: managed behavior only, no native engine required.
// Style follows BiscuitSharp (plain Exe runner, no test-framework dependency).
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MinigrafSharp;
using MinigrafSharp.Native;

int failures = 0;

void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    if (!condition)
    {
        failures++;
    }
}

// 1. RID selection matrix (pure function, no environment access).
Check(NativeLoader.SelectRuntimeIdentifier(true, false, false, Architecture.X64) == "win-x64", "rid win-x64");
Check(NativeLoader.SelectRuntimeIdentifier(false, true, false, Architecture.X64) == "linux-x64", "rid linux-x64");
Check(NativeLoader.SelectRuntimeIdentifier(false, true, false, Architecture.Arm64) == "linux-arm64", "rid linux-arm64");
Check(NativeLoader.SelectRuntimeIdentifier(false, false, true, Architecture.X64) == "osx-x64", "rid osx-x64");
Check(NativeLoader.SelectRuntimeIdentifier(false, false, true, Architecture.Arm64) == "osx-arm64", "rid osx-arm64");
Check(NativeLoader.SelectRuntimeIdentifier(false, false, false, Architecture.X64) is null, "rid unknown os -> null");
Check(NativeLoader.SelectRuntimeIdentifier(true, false, false, Architecture.Arm64) is null, "rid win-arm64 unsupported -> null");
Check(NativeLoader.SelectRuntimeIdentifier(true, false, false, Architecture.X86) is null, "rid win-x86 unsupported -> null");

// 2. Native filenames.
Check(NativeLoader.GetNativeFileName("win-x64") == "minigraf.dll", "filename win-x64");
Check(NativeLoader.GetNativeFileName("linux-x64") == "libminigraf.so", "filename linux-x64");
Check(NativeLoader.GetNativeFileName("linux-arm64") == "libminigraf.so", "filename linux-arm64");
Check(NativeLoader.GetNativeFileName("osx-x64") == "libminigraf.dylib", "filename osx-x64");
Check(NativeLoader.GetNativeFileName("osx-arm64") == "libminigraf.dylib", "filename osx-arm64");

// 3. Override resolution: relative paths root against the current directory.
string relativeResolved = NativeLoader.ResolveOverridePath("relative/native.dll", "minigraf.dll");
Check(
    Path.IsPathRooted(relativeResolved) &&
    relativeResolved == Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "relative/native.dll")),
    "relative override resolves against current directory");

// A directory override gains the RID filename.
string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
Directory.CreateDirectory(tempDir);
try
{
    string dirResolved = NativeLoader.ResolveOverridePath(tempDir, "minigraf.dll");
    Check(dirResolved == Path.Combine(tempDir, "minigraf.dll"), "directory override gains filename");
}
finally
{
    Directory.Delete(tempDir);
}

// An absolute file override passes through unchanged.
string absFile = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    ? @"C:\tmp\minigraf.dll"
    : "/tmp/minigraf.dll";
Check(NativeLoader.ResolveOverridePath(absFile, "minigraf.dll") == absFile, "absolute file override passes through");

// 4. Candidate probing is deterministic: package location first, then base dir.
string[] candidates = NativeLoader.GetCandidatePaths("win-x64", "minigraf.dll");
Check(candidates.Length >= 2, "candidate list has package + base entries");
Check(
    candidates[0] == Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "minigraf.dll"),
    "first candidate is runtimes/<rid>/native");
Check(candidates[1] == Path.Combine(AppContext.BaseDirectory, "minigraf.dll"), "second candidate is base dir file");

// 5. Pinned constants match native/upstream.json (provenance, no native load).
string? repoFile = FindRepoFile(["native", "upstream.json"]);
Check(repoFile is not null, "native/upstream.json found from output dir");
if (repoFile is not null)
{
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(repoFile));
    JsonElement root = manifest.RootElement;
    Check(root.GetProperty("minigraf").GetProperty("version").GetString() == NativeLoader.PinnedMinigrafVersion, "minigraf version pinned");
    Check(root.GetProperty("minigraf").GetProperty("commit").GetString() == NativeLoader.PinnedMinigrafCommit, "minigraf commit pinned");
    Check(root.GetProperty("minigraf-c").GetProperty("version").GetString() == NativeLoader.PinnedMinigrafCVersion, "minigraf-c version pinned");
    Check(root.GetProperty("minigraf-c").GetProperty("commit").GetString() == NativeLoader.PinnedMinigrafCCommit, "minigraf-c commit pinned");

    // 6. Archived header bytes hash to the recorded manifest value.
    string headerRel = root.GetProperty("header").GetProperty("path").GetString()!;
    string headerSha = root.GetProperty("header").GetProperty("sha256").GetString()!;
    string? headerFile = FindRepoFile(headerRel.Split('/'));
    Check(headerFile is not null, "archived minigraf.h found");
    if (headerFile is not null)
    {
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(headerFile)));
        Check(string.Equals(actual, headerSha, StringComparison.OrdinalIgnoreCase), "archived header hash matches manifest");
    }
}

// 7. Public surface leaks no native handles.
Type[] exported = typeof(MinigrafDatabase).Assembly.GetExportedTypes();
Check(
    exported.Select(t => t.FullName).Order().SequenceEqual(
        new[]
        {
            "MinigrafSharp.MinigrafDatabase",
            "MinigrafSharp.MinigrafException",
            "MinigrafSharp.MinigrafNativeLoadException",
            "MinigrafSharp.MinigrafOpenException",
        }.Order()),
    "public types are exactly the M0 surface");
Check(exported.All(t => t.Namespace == "MinigrafSharp"), "no public Interop/Native types");
bool leaksNative = false;
foreach (Type type in exported)
{
    foreach (MemberInfo member in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Concat<MemberInfo>(type.GetConstructors())
        .Concat(type.GetProperties()))
    {
        foreach (Type t in MemberTypes(member))
        {
            if (t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsPointer || typeof(SafeHandle).IsAssignableFrom(t))
            {
                Console.WriteLine($"  leak: {type.FullName}.{member.Name} uses {t}");
                leaksNative = true;
            }
        }
    }
}
Check(!leaksNative, "no IntPtr/SafeHandle in public signatures");

Console.WriteLine(failures == 0 ? "ALL UNIT TESTS PASSED" : $"{failures} UNIT TEST(S) FAILED");
return failures == 0 ? 0 : 1;

static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
{
    MethodInfo m => m.ReturnType == typeof(void)
        ? m.GetParameters().Select(p => p.ParameterType)
        : m.GetParameters().Select(p => p.ParameterType).Prepend(m.ReturnType),
    ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
    PropertyInfo p => [p.PropertyType],
    _ => [],
};

static string? FindRepoFile(string[] relative)
{
    string? dir = AppContext.BaseDirectory;
    for (int i = 0; i < 10 && dir is not null; i++)
    {
        string candidate = dir;
        foreach (string part in relative)
        {
            candidate = Path.Combine(candidate, part);
        }

        if (File.Exists(candidate))
        {
            return candidate;
        }

        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    return null;
}
