# MinigrafSharp

An idiomatic, safe .NET binding for [Minigraf](https://github.com/project-minigraf/minigraf),
an embedded single-file bi-temporal graph database with Datalog queries.

> MinigrafSharp is an independent .NET binding for Minigraf and is not
> currently an official Project Minigraf package.

> Status: preview release candidate `0.1.0-preview.3`, qualified by CI for
> `win-x64`, `linux-x64` and `osx-arm64` (`net8.0`/`net10.0`, NativeAOT on
> net10.0). The public API is `MinigrafDatabase.OpenMemory()` /
> `Open(string)` plus `ExecuteRaw(string)` returning raw native JSON,
> `Execute(string)` returning typed `MinigrafResult` values, and
> `Checkpoint()` for explicit WAL flushes, against the pinned native engine.
> The package is not yet on NuGet.org. See
> [docs/product-spec.md](docs/product-spec.md) and [ROADMAP.md](ROADMAP.md).

> MinigrafSharp currently targets Minigraf 2.0.5. The upstream v2 storage
> engine has known edge cases around specific transaction, temporal, and
> crash/recovery scenarios. See [docs/v2-qualification.md](docs/v2-qualification.md).

## Install

When published, install with no native setup, Rust, or environment
variables:

```sh
dotnet add package MinigrafSharp
```

Until then, build the preview package locally (see
[docs/packaging.md](docs/packaging.md)):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Get-Native.ps1 -Rid win-x64
dotnet pack src/MinigrafSharp/MinigrafSharp.csproj `
  -c Release -p:MinigrafSharpEnablePack=true -o artifacts/packages
```

## Try it

Prerequisites: .NET 8 or .NET 10 SDK.

```powershell
# From a repository checkout: fetch the pinned native asset, then run.
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Get-Native.ps1
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

// File-backed use with an explicit checkpoint.
using (var fileDb = MinigrafDatabase.Open("example.minigraf"))
{
    fileDb.Execute(
        """(transact [[:alice :name "Alice"]])""");

    fileDb.Checkpoint();
}
```

Native refs and keywords arrive as strings at this boundary, and
non-finite floats arrive as null; see
[docs/native-boundary.md](docs/native-boundary.md).

Pinned baseline: Minigraf core `v2.0.5`, minigraf-c `v2.0.5`
(see [native/upstream.json](native/upstream.json)).
Qualified and packaged platform: `win-x64` (lifecycle, raw/typed execution,
file persistence, storage/recovery/temporal qualification, NativeAOT,
trimming). No backup or crash-safety guarantees are claimed beyond the
qualified observations in [docs/v2-qualification.md](docs/v2-qualification.md).

## Learn more

- [Product spec](docs/product-spec.md): goals, non-goals, milestones.
- [Packaging](docs/packaging.md): package layout, RID support, provenance, verifiers.
- [Architecture](docs/architecture.md): managed/native boundary and ownership.
- [Native boundary](docs/native-boundary.md): exact C ABI, provenance, loading.
- [Compatibility](docs/compatibility.md): RID status, wrapper/core matrix.
- [v2.0.5 qualification](docs/v2-qualification.md): reliability boundary and known upstream limitations.
- [Hardening](docs/hardening.md): stress, loader/tamper matrices, privacy, release decision.
- [Verification](docs/verification.md): executed M0–M2.5 evidence.
- [Decision 0001](docs/decisions/0001-wrap-official-minigraf-c.md): why we wrap minigraf-c.
- [ROADMAP.md](ROADMAP.md) and [CHANGELOG.md](CHANGELOG.md).

## License and attribution

MinigrafSharp is [Apache-2.0](LICENSE); see [NOTICE](NOTICE) for upstream attribution.
Minigraf/minigraf-c are MIT OR Apache-2.0.
