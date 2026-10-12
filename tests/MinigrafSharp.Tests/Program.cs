// M1b unit tests: managed behavior only, no native engine required.
// Style follows BiscuitSharp (plain Exe runner, no test-framework dependency).
using System.Collections;
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

static bool Throws<TException>(Func<object?> action)
    where TException : Exception
{
    try
    {
        action();
        return false;
    }
    catch (TException)
    {
        return true;
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
            "MinigrafSharp.MinigrafCheckpointException",
            "MinigrafSharp.MinigrafException",
            "MinigrafSharp.MinigrafExecutionException",
            "MinigrafSharp.MinigrafNativeLoadException",
            "MinigrafSharp.MinigrafOkResult",
            "MinigrafSharp.MinigrafOpenException",
            "MinigrafSharp.MinigrafQueryResult",
            "MinigrafSharp.MinigrafResult",
            "MinigrafSharp.MinigrafResultException",
            "MinigrafSharp.MinigrafRetractionResult",
            "MinigrafSharp.MinigrafRow",
            "MinigrafSharp.MinigrafTransactionResult",
            "MinigrafSharp.MinigrafValue",
            "MinigrafSharp.MinigrafValueKind",
        }.Order()),
    "public types are exactly the M1c surface");
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

// 8. Open() argument validation happens before any native call (no engine needed).
Check(Throws<ArgumentNullException>(() => MinigrafDatabase.Open(null!)), "Open null rejected");
Check(Throws<ArgumentException>(() => MinigrafDatabase.Open("")), "Open empty rejected");

// 8. Decoder valid shapes (managed JSON strings, no native engine).
Check(
    MinigrafResultDecoder.Decode("{\"transacted\":42}") is MinigrafTransactionResult { TransactionId: 42UL },
    "decode transacted");
Check(
    MinigrafResultDecoder.Decode("{\"retracted\":43}") is MinigrafRetractionResult { TransactionId: 43UL },
    "decode retracted");
Check(
    ReferenceEquals(MinigrafResultDecoder.Decode("{\"ok\":true}"), MinigrafOkResult.Instance),
    "decode ok");
Check(
    MinigrafResultDecoder.Decode("{\"transacted\":1,\"future_metadata\":\"x\"}")
        is MinigrafTransactionResult { TransactionId: 1UL },
    "unknown extra field tolerated");

MinigrafQueryResult people = (MinigrafQueryResult)MinigrafResultDecoder.Decode(
    "{\"variables\":[\"?name\",\"?age\"],\"results\":[[\"Alice\",30],[\"Bob\",41]]}");
Check(people.Variables.Count == 2 && people.Rows.Count == 2, "decode query shape");
Check(people.Rows[0]["?name"].GetString() == "Alice", "decode query string cell");
Check(people.Rows[0]["?age"].GetInt64() == 30, "decode query integer cell");
Check(people.Rows[1][0].GetString() == "Bob", "decode positional access");
Check(people.Rows[1][1].GetInt64() == 41, "decode second row");

// 9. Scalar coverage through one wide row.
MinigrafQueryResult scalars = (MinigrafQueryResult)MinigrafResultDecoder.Decode(
    "{\"variables\":[\"?s\",\"?neg\",\"?zero\",\"?pos\",\"?f\",\"?t\",\"?fl\",\"?n\",\"?u\"]," +
    "\"results\":[[\"s\",-5,0,42,3.14,true,false,null,\"\\u00fc\\u2744\"]]}");
MinigrafRow srow = scalars.Rows[0];
Check(srow["?s"].Kind == MinigrafValueKind.String && srow["?s"].GetString() == "s", "scalar string");
Check(srow["?neg"].Kind == MinigrafValueKind.Integer && srow["?neg"].GetInt64() == -5, "scalar negative integer");
Check(srow["?zero"].GetInt64() == 0, "scalar zero");
Check(srow["?pos"].GetInt64() == 42, "scalar positive integer");
Check(srow["?f"].Kind == MinigrafValueKind.Float && srow["?f"].GetDouble() == 3.14, "scalar float");
Check(srow["?t"].Kind == MinigrafValueKind.Boolean && srow["?t"].GetBoolean(), "scalar true");
Check(!srow["?fl"].GetBoolean(), "scalar false");
Check(srow["?n"].Kind == MinigrafValueKind.Null && srow["?n"].IsNull, "scalar null");
Check(srow["?u"].GetString() == "ü❄", "scalar unicode escapes decoded");
Check(!srow["?s"].IsNull, "non-null IsNull is false");

// 10. Value equality is kind-sensitive; wrong-kind access throws, never coerces.
MinigrafValue a1 = ((MinigrafQueryResult)MinigrafResultDecoder.Decode(
    "{\"variables\":[\"?v\"],\"results\":[[\"Alice\"]]}")).Rows[0]["?v"];
MinigrafValue a2 = ((MinigrafQueryResult)MinigrafResultDecoder.Decode(
    "{\"variables\":[\"?v\"],\"results\":[[\"Alice\"]]}")).Rows[0]["?v"];
MinigrafValue i42 = ((MinigrafQueryResult)MinigrafResultDecoder.Decode(
    "{\"variables\":[\"?v\"],\"results\":[[42]]}")).Rows[0]["?v"];
MinigrafValue f42 = ((MinigrafQueryResult)MinigrafResultDecoder.Decode(
    "{\"variables\":[\"?v\"],\"results\":[[42.0]]}")).Rows[0]["?v"];
Check(a1 == a2 && a1.Equals(a2) && a1.GetHashCode() == a2.GetHashCode(), "equal values are equal");
Check(i42 != f42, "integer 42 does not equal float 42.0");
Check(MinigrafValue.Null == MinigrafValue.Null && MinigrafValue.Null.IsNull, "null equals null");
Check(Throws<InvalidOperationException>(() => a1.GetInt64()), "string.GetInt64 throws");
Check(Throws<InvalidOperationException>(() => i42.GetString()), "integer.GetString throws");
Check(Throws<InvalidOperationException>(() => i42.GetDouble()), "integer.GetDouble throws");
Check(Throws<InvalidOperationException>(() => f42.GetInt64()), "float.GetInt64 throws");
Check(Throws<InvalidOperationException>(() => MinigrafValue.Null.GetBoolean()), "null.GetBoolean throws");

// 11. Row access distinguishes missing variables from null values.
Check(srow.TryGetValue("?s", out MinigrafValue found) && found.GetString() == "s", "TryGetValue hit");
Check(!srow.TryGetValue("?missing", out _) && !srow.TryGetValue(null!, out _), "TryGetValue miss is false");
Check(Throws<KeyNotFoundException>(() => srow["?missing"]), "unknown variable throws KeyNotFound");
Check(Throws<ArgumentOutOfRangeException>(() => srow[9]), "index past end throws");
Check(Throws<ArgumentOutOfRangeException>(() => srow[-1]), "negative index throws");
Check(Throws<ArgumentNullException>(() => srow[null!]), "null variable throws ArgumentNull");
Check(srow.Count == 9, "row count matches variables");

// 12. Result collections are immutable snapshots, not leaked backing lists.
Check(scalars.Variables is not List<string> && scalars.Variables is not string[], "variables not mutable list/array");
Check(scalars.Rows is not List<MinigrafRow> && scalars.Rows is not MinigrafRow[], "rows not mutable list/array");
Check(((IList)scalars.Variables).IsReadOnly && ((IList)scalars.Rows).IsReadOnly, "exposed collections are read-only");

// 13. Malformed shapes fail closed with MinigrafResultException.
string[] badShapes =
[
    "not json",
    "{}",
    "[]",
    "\"str\"",
    "null",
    "42",
    "{\"transacted\":1,\"retracted\":2}",
    "{\"transacted\":\"1\"}",
    "{\"transacted\":-1}",
    "{\"transacted\":1.5}",
    "{\"transacted\":18446744073709551616}",
    "{\"retracted\":\"2\"}",
    "{\"ok\":false}",
    "{\"ok\":null}",
    "{\"ok\":\"true\"}",
    "{\"variables\":[\"?x\"]}",
    "{\"results\":[[1]]}",
    "{\"transacted\":1,\"results\":[]}",
    "{\"variables\":\"?x\",\"results\":[]}",
    "{\"variables\":[1],\"results\":[]}",
    "{\"variables\":[\"?x\"],\"results\":{}}",
    "{\"variables\":[\"?x\"],\"results\":[1]}",
    "{\"variables\":[\"?x\"],\"results\":[[1,2]]}",
    "{\"variables\":[\"?x\",\"?x\"],\"results\":[[1,2]]}",
    "{\"variables\":[\"?x\"],\"results\":[[{}]]}",
    "{\"variables\":[\"?x\"],\"results\":[[[1]]]}",
    "{\"variables\":[],\"results\":[[18446744073709551616]]}",
];
int rejected = 0;
foreach (string bad in badShapes)
{
    try
    {
        MinigrafResultDecoder.Decode(bad);
        Console.WriteLine($"  accepted (should reject): {bad}");
    }
    catch (MinigrafResultException)
    {
        rejected++;
    }
}
Check(rejected == badShapes.Length, $"malformed shapes fail closed ({rejected}/{badShapes.Length})");
Check(Throws<ArgumentNullException>(() => MinigrafResultDecoder.Decode(null!)), "decode null rejected");

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
