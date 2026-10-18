using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MinigrafSharp;

internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> FailingCases = [];

    private static void Main()
    {
        Console.WriteLine("runtime identity qualification (M3.2f):");
        Console.WriteLine("  surface: MinigrafRuntimeIdentity.Current (verified shim token + package version)");
        Console.WriteLine("  scope: host RID, net8.0 and net10.0");

        Cases();
        Console.WriteLine($"pass={_pass} fail={_fail}");
        if (_fail != 0)
        {
            Console.WriteLine("failed case: " + string.Join(", ", FailingCases));
            Environment.Exit(1);
        }

        Console.WriteLine("ALL RUNTIME-IDENTITY QUALIFICATION TESTS PASSED");
    }

    private static void Cases()
    {
        // Order invariant: the refusal probe runs before any successful shim
        // load in this process, so it exercises the first-load rejection path.
        Case("official binary refused as shim, then valid identity loads (no poisoning)", RefusalThenRecovery);
        Case("identity matches upstream provenance, identity.json and package version", MatchesProvenance);
        Case("identity is immutable and stable across calls", StableAcrossCalls);
        Case("token is closed and structurally validated", ClosedToken);
        Case("malformed JSON is rejected", MalformedJson);
        Case("missing fields are rejected", MissingFields);
        Case("wrong field types are rejected", WrongTypes);
        Case("empty fields are rejected", EmptyFields);
        Case("incorrect shim ABI is rejected", WrongAbi);
        Case("incorrect shim source revision is rejected", WrongRevision);
        Case("incorrect core version is rejected", WrongCoreVersion);
        Case("incorrect core commit is rejected", WrongCoreCommit);
        Case("unexpected token properties are rejected", UnexpectedProperty);
        Case("identity token is provenance, not a binary hash", ProvenanceNotHash);
    }

    private static void RefusalThenRecovery()
    {
        string official = OfficialAssetPath();
        Check(File.Exists(official), "official asset staged for refusal probe: " + official);
        string? previous = Environment.GetEnvironmentVariable("MINIGRAFSHARP_SHIM_NATIVE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("MINIGRAFSHARP_SHIM_NATIVE_PATH", official);
            MinigrafNativeLoadException? refusal = null;
            try
            {
                _ = MinigrafRuntimeIdentity.Current;
            }
            catch (MinigrafNativeLoadException ex)
            {
                refusal = ex;
            }

            Check(refusal is not null, "official binary under the shim import is refused");
            Console.WriteLine("  observed (refusal): " + refusal!.Message);
        }
        finally
        {
            if (previous is null)
            {
                Environment.SetEnvironmentVariable("MINIGRAFSHARP_SHIM_NATIVE_PATH", null);
            }
            else
            {
                Environment.SetEnvironmentVariable("MINIGRAFSHARP_SHIM_NATIVE_PATH", previous);
            }
        }

        // Failed candidates are never cached: the verified shim still loads.
        var identity = MinigrafRuntimeIdentity.Current;
        Check(identity.CoreVersion.Length > 0, "valid identity loads after the refused candidate");
    }

    private static void MatchesProvenance()
    {
        var upstream = JsonDocument.Parse(File.ReadAllText(UpstreamJsonPath())).RootElement;
        var identityFile = JsonDocument.Parse(File.ReadAllText(IdentityJsonPath())).RootElement;
        var identity = MinigrafRuntimeIdentity.Current;

        Check(identity.ShimAbiVersion == identityFile.GetProperty("shim_abi").GetString(),
            "shim ABI matches identity.json");
        Check(identity.ShimSourceRevision == identityFile.GetProperty("source_revision").GetInt32(),
            "source revision matches identity.json");
        Check(identity.CoreVersion == upstream.GetProperty("minigraf").GetProperty("version").GetString(),
            "core version matches upstream.json");
        Check(identity.CoreCommit == upstream.GetProperty("minigraf").GetProperty("commit").GetString(),
            "core commit matches upstream.json");

        string informational = typeof(MinigrafRuntimeIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion!;
        int plus = informational.IndexOf('+');
        string expectedPackage = plus >= 0 ? informational[..plus] : informational;
        Check(identity.PackageVersion == expectedPackage,
            $"package version '{identity.PackageVersion}' equals '{expectedPackage}'");
        Check(identity.PackageVersion.StartsWith("0.1.0-preview.", StringComparison.Ordinal),
            "package version is the preview line: " + identity.PackageVersion);
    }

    private static void StableAcrossCalls()
    {
        var first = MinigrafRuntimeIdentity.Current;
        var second = MinigrafRuntimeIdentity.Current;
        Check(first.PackageVersion == second.PackageVersion &&
              first.ShimAbiVersion == second.ShimAbiVersion &&
              first.ShimSourceRevision == second.ShimSourceRevision &&
              first.CoreVersion == second.CoreVersion &&
              first.CoreCommit == second.CoreCommit,
            "repeated reads return the same immutable values");
    }

    private static void ClosedToken()
    {
        // The pinned token is the closed shape the loader verified.
        string token = ExpectedToken();
        var identity = MinigrafRuntimeIdentity.Current;
        Check(identity.ShimAbiVersion == "0.1.0" &&
              identity.ShimSourceRevision == 1 &&
              identity.CoreVersion == "2.0.7",
            "token fields: " + token);
    }

    private static void MalformedJson()
    {
        ExpectInvalid("not-json", "malformed JSON");
        ExpectInvalid("[]", "non-object root");
        ExpectInvalid("{\"shim_abi\":\"0.1.0\"", "truncated JSON");
    }

    private static void MissingFields()
    {
        foreach (string field in new[] { "shim_abi", "shim_source_revision", "core_version", "core_commit" })
        {
            ExpectInvalid(RemoveField(ExpectedToken(), field), "missing " + field);
        }
    }

    private static void WrongTypes()
    {
        string token = ExpectedToken();
        ExpectInvalid(token.Replace("\"0.1.0\"", "1", StringComparison.Ordinal), "numeric shim ABI");
        ExpectInvalid(token.Replace("\"shim_source_revision\":1", "\"shim_source_revision\":\"1\"", StringComparison.Ordinal),
            "string revision");
        ExpectInvalid(token.Replace("\"core_commit\":\"2d2e5d75f28e91d61be9f293de895e2271e62a29\"",
                "\"core_commit\":42", StringComparison.Ordinal), "numeric core commit");
    }

    private static void EmptyFields()
    {
        string token = ExpectedToken();
        ExpectInvalid(token.Replace("\"0.1.0\"", "\"\"", StringComparison.Ordinal), "empty shim ABI");
        ExpectInvalid(token.Replace("\"2.0.7\"", "\"\"", StringComparison.Ordinal), "empty core version");
        ExpectInvalid(token.Replace("\"2d2e5d75f28e91d61be9f293de895e2271e62a29\"", "\"\"", StringComparison.Ordinal),
            "empty core commit");
    }

    private static void WrongAbi()
    {
        ExpectInvalid(ExpectedToken().Replace("\"0.1.0\"", "\"9.9.9\"", StringComparison.Ordinal), "wrong ABI");
    }

    private static void WrongRevision()
    {
        ExpectInvalid(ExpectedToken().Replace("\"shim_source_revision\":1", "\"shim_source_revision\":2", StringComparison.Ordinal),
            "wrong revision");
    }

    private static void WrongCoreVersion()
    {
        ExpectInvalid(ExpectedToken().Replace("\"2.0.7\"", "\"9.9.9\"", StringComparison.Ordinal), "wrong core version");
    }

    private static void WrongCoreCommit()
    {
        ExpectInvalid(ExpectedToken().Replace("2d2e5d75f28e91d61be9f293de895e2271e62a29",
                "0000000000000000000000000000000000000000", StringComparison.Ordinal),
            "wrong core commit");
    }

    private static void UnexpectedProperty()
    {
        string token = ExpectedToken().TrimEnd('}') + ",\"extra\":1}";
        ExpectInvalid(token, "unexpected property");
    }

    private static void ProvenanceNotHash()
    {
        var identity = MinigrafRuntimeIdentity.Current;
        Check(identity.CoreCommit.Length == 40 &&
              identity.CoreCommit.All(c => Uri.IsHexDigit(c)),
            "core commit is the pinned 40-hex revision: " + identity.CoreCommit);
        Check(identity.CoreCommit != identity.PackageVersion,
            "identity exposes provenance, not the package version as a hash");
    }

    // --- helpers ---------------------------------------------------------

    private static void ExpectInvalid(string token, string label)
    {
        MinigrafNativeLoadException? error = null;
        try
        {
            _ = MinigrafRuntimeIdentity.ParseAndValidate(token);
        }
        catch (MinigrafNativeLoadException ex)
        {
            error = ex;
        }

        Check(error is not null, label + " must be rejected");
        Check(!string.IsNullOrWhiteSpace(error!.Message), label + " rejection keeps a diagnostic: " + error.Message);
    }

    private static string ExpectedToken()
    {
        var upstream = JsonDocument.Parse(File.ReadAllText(UpstreamJsonPath())).RootElement;
        var identity = JsonDocument.Parse(File.ReadAllText(IdentityJsonPath())).RootElement;
        return "{\"shim_abi\":\"" + identity.GetProperty("shim_abi").GetString() + "\"," +
               "\"shim_source_revision\":" + identity.GetProperty("source_revision").GetInt32() + "," +
               "\"core_version\":\"" + upstream.GetProperty("minigraf").GetProperty("version").GetString() + "\"," +
               "\"core_commit\":\"" + upstream.GetProperty("minigraf").GetProperty("commit").GetString() + "\"}";
    }

    private static string RemoveField(string token, string field)
    {
        using var document = JsonDocument.Parse(token);
        var parts = new List<string>();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name == field)
            {
                continue;
            }

            parts.Add(property.Value.ValueKind == JsonValueKind.Number
                ? "\"" + property.Name + "\":" + property.Value.GetRawText()
                : "\"" + property.Name + "\":" + JsonSerializer.Serialize(property.Value.GetString()));
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static string OfficialAssetPath()
    {
        string rid;
        string fileName;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            rid = "win-x64";
            fileName = "minigraf.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            rid = "osx-arm64";
            fileName = "libminigraf.dylib";
        }
        else
        {
            rid = "linux-x64";
            fileName = "libminigraf.so";
        }

        return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);
    }

    private static string UpstreamJsonPath() => FindUp("native", "upstream.json");

    private static string IdentityJsonPath() => FindUp("native", "shim", "identity.json");

    private static string FindUp(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine([dir.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not locate " + string.Join("/", segments));
    }

    private static void Case(string name, Action body)
    {
        try
        {
            body();
            _pass++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _fail++;
            FailingCases.Add(name);
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertEx(message);
        }
    }

    private sealed class AssertEx(string message) : Exception(message);
}
