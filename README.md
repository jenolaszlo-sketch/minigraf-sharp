# MinigrafSharp

An idiomatic, safe .NET binding for [Minigraf](https://github.com/project-minigraf/minigraf),
an embedded single-file bi-temporal graph database with Datalog queries.

> MinigrafSharp is an independent .NET binding for Minigraf and is not
> currently an official Project Minigraf package.

> Status: M1b (typed results). The public API is
> `MinigrafDatabase.OpenMemory()` plus `ExecuteRaw(string)` returning raw
> native JSON and `Execute(string)` returning typed `MinigrafResult` values
> against the pinned native engine.
> There is no NuGet package yet. See [docs/product-spec.md](docs/product-spec.md)
> and [ROADMAP.md](ROADMAP.md).

## Try it (M1b)

Prerequisites: .NET 8 or .NET 10 SDK.

```powershell
# Fetch and verify the pinned native asset for this machine (win-x64).
pwsh eng/Get-Native.ps1

dotnet build MinigrafSharp.slnx -c Release
dotnet run --project samples/MinigrafSharp.QuickStart -c Release -f net10.0
```

```csharp
using MinigrafSharp;

using var db = MinigrafDatabase.OpenMemory();

db.Execute(
    """(transact [[:alice :name "Alice"]])""");

var result = (MinigrafQueryResult)db.Execute(
    """(query [:find ?name :where [?e :name ?name]])""");

Console.WriteLine(result.Rows[0]["?name"].GetString());

// Raw escape hatch: exact native JSON, no decoding.
string raw = db.ExecuteRaw(
    """(query [:find ?name :where [?e :name ?name]])""");
```

Native refs and keywords arrive as strings at this boundary, and
non-finite floats arrive as null; see
[docs/native-boundary.md](docs/native-boundary.md).

Pinned baseline: Minigraf core `v2.0.5`, minigraf-c `v2.0.5`
(see [native/upstream.json](native/upstream.json)).
Verified platform for M1b: `win-x64`.

## Learn more

- [Product spec](docs/product-spec.md): goals, non-goals, milestones.
- [Architecture](docs/architecture.md): managed/native boundary and ownership.
- [Native boundary](docs/native-boundary.md): exact C ABI, provenance, loading.
- [Compatibility](docs/compatibility.md): wrapper/core/C-binding matrix.
- [Verification](docs/verification.md): executed M0/M1a/M1b evidence.
- [Decision 0001](docs/decisions/0001-wrap-official-minigraf-c.md): why we wrap minigraf-c.
- [ROADMAP.md](ROADMAP.md) and [CHANGELOG.md](CHANGELOG.md).

## License and attribution

MinigrafSharp is [Apache-2.0](LICENSE); see [NOTICE](NOTICE) for upstream attribution.
Minigraf/minigraf-c are MIT OR Apache-2.0.
