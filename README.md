# MinigrafSharp

An idiomatic, safe .NET binding for [Minigraf](https://github.com/project-minigraf/minigraf),
an embedded single-file bi-temporal graph database with Datalog queries.

> MinigrafSharp is an independent .NET binding for Minigraf and is not
> currently an official Project Minigraf package.

> Status: M1a (raw execution). The public API is
> `MinigrafDatabase.OpenMemory()` plus `ExecuteRaw(string)` returning raw
> native JSON against the pinned native engine.
> There is no NuGet package yet. See [docs/product-spec.md](docs/product-spec.md)
> and [ROADMAP.md](ROADMAP.md).

## Try it (M1a)

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

string result = db.ExecuteRaw(
    """(transact [[:alice :name "Alice"]])""");

Console.WriteLine(result);
```

Pinned baseline: Minigraf core `v2.0.5`, minigraf-c `v2.0.5`
(see [native/upstream.json](native/upstream.json)).
Verified platform for M1a: `win-x64`.

## Learn more

- [Product spec](docs/product-spec.md): goals, non-goals, milestones.
- [Architecture](docs/architecture.md): managed/native boundary and ownership.
- [Native boundary](docs/native-boundary.md): exact C ABI, provenance, loading.
- [Compatibility](docs/compatibility.md): wrapper/core/C-binding matrix.
- [Verification](docs/verification.md): executed M0/M1a evidence.
- [Decision 0001](docs/decisions/0001-wrap-official-minigraf-c.md): why we wrap minigraf-c.
- [ROADMAP.md](ROADMAP.md) and [CHANGELOG.md](CHANGELOG.md).

## License and attribution

MinigrafSharp is [Apache-2.0](LICENSE); see [NOTICE](NOTICE) for upstream attribution.
Minigraf/minigraf-c are MIT OR Apache-2.0.
