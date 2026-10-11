# MinigrafSharp product and implementation specification

Status: initial design
Date: 2026-10-11

## 1. Purpose

`MinigrafSharp` is an idiomatic, safe .NET binding for [Minigraf](https://github.com/project-minigraf/minigraf), an embedded single-file bi-temporal graph database with Datalog queries.

The package exists for two reasons:

1. give .NET applications first-class access to Minigraf without requiring users to write P/Invoke or install Rust; and
2. provide a reusable public binding that remains useful even if the original Penghou/Guyabano use case changes.

The initial motivation is agent memory and temporal knowledge: storing facts, corrections and changing beliefs while preserving what was known or considered true at an earlier point in time. The package itself must remain independent of Penghou, Guyabano, Cangjie, LatticeDB and any particular agent architecture.

The wrapper should preserve Minigraf semantics rather than invent a new database framework.

## 2. Product position

MinigrafSharp is not a replacement for LatticeDbSharp and must not attempt to make the two databases look identical.

Their useful capabilities are different:

- LatticeDB is well suited to graph, vector and lexical retrieval.
- Minigraf is well suited to Datalog, recursive relationships and bi-temporal facts, including transaction time and valid time.

A consumer may use either database independently or compose both at a higher layer. MinigrafSharp must not contain an abstraction such as `IGraphDatabase` merely to make that composition appear uniform.

For an agent-memory system, a possible higher-level composition is:

```text
semantic / lexical / graph retrieval   -> LatticeDB
historical facts / changing beliefs    -> Minigraf
application composition                -> consumer
```

That composition belongs to the application, not this package.

## 3. Upstream baseline

Initial development baseline:

| Component | Baseline |
| --- | --- |
| Minigraf core | `v2.0.5` |
| Minigraf core commit | `a861e551162623c8f4752f94daf5b637543f8358` |
| minigraf-c | `v2.0.5` |
| minigraf-c commit | `8422f9f048d8821a759cbe9e1c2ea6b8f53621dc` |
| minigraf-c core dependency | `minigraf =2.0.5` |
| C header | `include/minigraf.h` from the pinned minigraf-c release |
| MinigrafSharp | initial `0.1.0-preview.*` line |

The baseline is exact. Do not float against `main`, a Cargo version range, an arbitrary system library or an unverified native binary.

### 3.1 v2.0.5 is a development baseline, not an unconditional stability claim

Minigraf documents several known v2.x data-integrity and recovery issues whose fixes require the v3 file format. Important examples include:

- multiple values of the same attribute for one entity written in one transaction can read back as one value;
- retracting and re-asserting the same fact in one explicit write transaction can lose the final assertion;
- repeated assertions can preserve overlapping valid-time windows unexpectedly;
- checkpoint is not crash-atomic on v2.x, although current v2 releases rebuild indexes after detecting the relevant condition;
- restoring an older `.graph` beside a newer `.wal` can replay an incompatible WAL generation.

The project must therefore treat v3.0.0 as an explicit upgrade candidate as soon as an official core and C-binding release is available.

A preview may be built and tested against v2.0.5, but documentation must not hide the upstream limitations. The wrapper must not claim to repair storage semantics that belong to the engine.

## 4. Binding strategy

### 4.1 Decision

Start by wrapping the official `minigraf-c` library directly.

Do not reimplement Minigraf storage, Datalog parsing, temporal semantics or query evaluation in C#.

Do not create a second Rust bridge merely for architectural symmetry with BiscuitSharp. Minigraf already publishes an official C FFI package and native assets.

The initial boundary is:

```text
.NET application
      |
MinigrafSharp public API
      |
managed result/error/lifetime layer
      |
internal LibraryImport interop
      |
official minigraf-c
      |
official Minigraf Rust core
```

### 4.2 Why this differs from BiscuitSharp

BiscuitSharp needed its own versioned Rust bridge because the upstream engine did not expose the required stable C contract.

Minigraf already has `minigraf-c`, so the smallest trustworthy design is to consume that official boundary and prove it thoroughly.

However, the current C API exposes only:

```text
minigraf_open
minigraf_open_in_memory
minigraf_close
minigraf_execute
minigraf_string_free
minigraf_checkpoint
minigraf_last_error
```

The Rust core has richer capabilities, including explicit write transactions, open options and prepared queries, that are not currently exposed through `minigraf-c`.

The managed API must therefore be designed so that a later native extension can add those capabilities without forcing an unrelated public API redesign.

### 4.3 Extension policy

If an important core feature is missing from `minigraf-c`, prefer these options in order:

1. contribute the capability upstream to `minigraf-c`;
2. wait for an upstream binding when the feature is not needed urgently;
3. only then add a small MinigrafSharp-owned native extension.

If a MinigrafSharp native extension becomes necessary, it must:

- wrap the official Minigraf Rust crate rather than reimplement engine semantics;
- have its own explicit ABI version;
- pin the core version, source commit, `Cargo.lock` and Rust toolchain;
- use bounded inputs/outputs and deterministic ownership;
- catch recoverable Rust unwinding before the C boundary;
- expose structured version identity;
- remain internal to the package;
- be justified by a concrete public API requirement.

A custom bridge is not part of M1 unless the official C ABI proves insufficient for the first usable release.

## 5. Goals

MinigrafSharp should:

1. provide a small, idiomatic .NET API over Minigraf;
2. make file-backed and in-memory databases easy to open and dispose;
3. expose Datalog execution without exposing P/Invoke or native pointers;
4. preserve Minigraf's temporal and query semantics rather than translating them into another query model;
5. return useful managed result types for transaction, retraction, success and query results;
6. preserve an escape hatch to the raw upstream JSON result;
7. map native failures into useful .NET exceptions without turning errors into successful results;
8. deterministically release every native database and returned native string;
9. package verified native assets for qualified .NET runtime identifiers;
10. pin and audit the exact upstream version and native asset identity;
11. support trimming and NativeAOT where the runtime and platform permit it;
12. document upstream limitations clearly;
13. be usable independently by any .NET application;
14. keep future prepared-query, write-transaction and richer temporal APIs possible without breaking the initial surface.

## 6. Non-goals

The first release should not implement:

- a LINQ-to-Datalog provider;
- an ORM or automatic CLR object persistence;
- a generic graph database interface shared with LatticeDB or Neo4j;
- a generic database abstraction shared with SQLite or DuckDB;
- a GraphRAG framework;
- embedding generation or vector search;
- a chat-memory framework;
- Penghou or Guyabano schemas;
- automatic fact extraction from LLM conversations;
- automatic conflict resolution between current and historical facts;
- replication or networking;
- fake asynchronous APIs implemented with `Task.Run`;
- claims of transactional or durability behavior beyond the pinned Minigraf engine;
- a managed reimplementation of Datalog or bi-temporal evaluation.

## 7. Repository and packages

Repository:

```text
https://github.com/jenolaszlo-sketch/minigraf-sharp
```

Primary package:

```text
MinigrafSharp
```

Primary namespace:

```text
MinigrafSharp
```

Primary public database type:

```text
MinigrafDatabase
```

Suggested repository structure:

```text
minigraf-sharp/
├── src/
│   └── MinigrafSharp/
│       ├── Interop/
│       ├── Native/
│       ├── Results/
│       ├── Errors/
│       └── MinigrafDatabase.cs
├── tests/
│   ├── MinigrafSharp.Tests/
│   └── MinigrafSharp.IntegrationTests/
├── samples/
│   ├── MinigrafSharp.QuickStart/
│   └── MinigrafSharp.AotSmoke/
├── native/
│   ├── include/
│   ├── upstream.json
│   ├── assets/
│   └── legal/
├── eng/
├── docs/
│   ├── product-spec.md
│   ├── architecture.md
│   ├── native-boundary.md
│   ├── compatibility.md
│   ├── verification.md
│   ├── public-api.txt
│   └── decisions/
├── Directory.Build.props
├── MinigrafSharp.slnx
├── README.md
├── ROADMAP.md
├── CHANGELOG.md
├── LICENSE
└── NOTICE
```

`Interop` and raw native handles remain internal.

## 8. Target frameworks

Initial managed targets:

```text
net8.0
net10.0
```

Use:

- nullable reference types;
- warnings as errors;
- deterministic managed builds;
- source-generated `LibraryImport` where it maps correctly to the C ABI;
- trimming-safe APIs where practical;
- no runtime reflection in the core API.

## 9. Public API shape

The first public surface should be deliberately small.

### 9.1 Database lifecycle

```csharp
using MinigrafSharp;

using var db = MinigrafDatabase.Open("agent-memory.graph");
```

and:

```csharp
using var db = MinigrafDatabase.OpenMemory();
```

The database type owns one native `MiniGrafDb*` through an internal `SafeHandle`.

Suggested initial surface:

```csharp
public sealed class MinigrafDatabase : IDisposable
{
    public static MinigrafDatabase Open(string path);
    public static MinigrafDatabase OpenMemory();

    public MinigrafResult Execute(string datalog);
    public string ExecuteRaw(string datalog);

    public void Checkpoint();
}
```

Names may change during M0 API review, but the semantics should remain this small for the first usable wrapper.

### 9.2 Execute semantics

`Execute` delegates Datalog semantics to Minigraf.

Examples:

```csharp
MinigrafResult write = db.Execute(
    """(transact [[:alice :person/name "Alice"]])""");

MinigrafResult query = db.Execute(
    """(query [:find ?name :where [?e :person/name ?name]])""");
```

Temporal behavior remains native Datalog:

```csharp
var past = db.Execute(
    """(query [:find ?age :as-of 1 :where [:alice :person/age ?age]])""");
```

MinigrafSharp must not invent a competing temporal query language.

### 9.3 Managed result model

The current official C binding serializes `QueryResult` as JSON with these result shapes:

```json
{"transacted": 1}
{"retracted": 2}
{"ok": true}
{"variables": ["?name"], "results": [["Alice"]]}
```

The wrapper should decode these into a closed managed result hierarchy while retaining `ExecuteRaw` for forward compatibility.

Suggested shape:

```csharp
public abstract record MinigrafResult;

public sealed record MinigrafTransactionResult(ulong TransactionId)
    : MinigrafResult;

public sealed record MinigrafRetractionResult(ulong TransactionId)
    : MinigrafResult;

public sealed record MinigrafOkResult
    : MinigrafResult;

public sealed record MinigrafQueryResult(
    IReadOnlyList<string> Variables,
    IReadOnlyList<MinigrafRow> Rows)
    : MinigrafResult;
```

`MinigrafRow` should support positional and variable-name access.

Example:

```csharp
var result = (MinigrafQueryResult)db.Execute(query);

foreach (MinigrafRow row in result.Rows)
{
    Console.WriteLine(row["?name"].GetString());
}
```

### 9.4 Value representation

Do not expose `JsonElement` ownership problems directly as the primary API.

Use an immutable managed scalar value with at least:

```text
String
Integer
Float
Boolean
Null
```

The current C JSON representation flattens Rust `Value::Ref` and `Value::Keyword` to JSON strings. Therefore M1 must not pretend it can distinguish those variants after crossing the official C boundary.

Document this explicitly.

If upstream later exposes typed values through the C ABI, MinigrafSharp may extend the managed value kind without changing existing meanings.

### 9.5 Raw result escape hatch

`ExecuteRaw` returns a managed UTF-8 string containing the exact JSON produced by the pinned native binding.

This exists for:

- forward compatibility;
- diagnostics;
- advanced consumers that want to own result decoding;
- testing parity against upstream language bindings.

The native pointer must always be copied to managed memory and freed with `minigraf_string_free` before returning.

## 10. Datalog and parameter safety

The initial binding accepts textual Datalog because that is what the official C ABI exposes.

Documentation must warn against interpolating untrusted values directly into Datalog source.

Do not create a home-grown query parameter syntax that Minigraf itself does not understand.

Minigraf core supports prepared statements with named bind slots, but `minigraf-c v2.0.5` does not expose them. Prepared queries are therefore a high-priority post-M1 capability.

Preferred path:

1. expose prepared queries upstream through `minigraf-c`;
2. then add an idiomatic managed `MinigrafPreparedQuery` API;
3. only introduce a MinigrafSharp native extension if upstream cannot provide the capability in a useful timeframe and a real consumer needs it.

Possible future surface:

```csharp
using MinigrafPreparedQuery query = db.Prepare(
    """(query [:find ?name :as-of $tx :where [$entity :person/name ?name]])""");

MinigrafQueryResult result = query.Execute(new Dictionary<string, MinigrafBindValue>
{
    ["tx"] = MinigrafBindValue.Transaction(4),
    ["entity"] = MinigrafBindValue.Entity(entityId)
});
```

This is not an M1 promise until the native boundary supports it correctly.

## 11. Explicit write transactions

Minigraf core supports `begin_write`, multiple operations and one commit, with implicit rollback on drop.

The official C API does not expose that object in v2.0.5.

Therefore M1 does not emulate explicit transactions by buffering Datalog in managed code. That could change semantics and durability behavior.

A future binding should expose the real native transaction when the FFI supports it:

```csharp
using MinigrafWriteTransaction tx = db.BeginWrite();

tx.Execute("...");
tx.Execute("...");
tx.Commit();
```

Required semantics when implemented:

- the native write lock is held according to Minigraf semantics;
- dispose without commit rolls back;
- commit and rollback have a deterministic terminal state;
- a managed wrapper must not silently add a second transaction layer;
- no ambient `System.Transactions` enlistment is implied.

## 12. Open options

Minigraf core v2.0.5 has `OpenOptions` for settings such as:

- WAL checkpoint threshold;
- page cache size;
- maximum derived facts;
- maximum query results;
- unlocked-filesystem behavior;
- synchronous/WAL durability mode.

The official C ABI currently exposes only `minigraf_open(path)` and `minigraf_open_in_memory()`.

M1 must not expose managed options that it cannot enforce.

A future `MinigrafDatabaseOptions` type should be added only when the native boundary can faithfully map the corresponding upstream options.

## 13. Errors

### 13.1 Initial taxonomy

Suggested public hierarchy:

```text
MinigrafException
├── MinigrafOpenException
├── MinigrafQueryException
├── MinigrafStorageException
├── MinigrafNativeException
└── MinigrafResultException
```

Do not over-classify when the official C ABI does not provide enough information.

`minigraf_execute` returns null on failure and `minigraf_last_error` returns a borrowed string valid until the next call on the same handle. The wrapper must copy the error immediately before another native operation can occur.

The current `minigraf-c` implementation serializes the Rust error to text, so M1 may only be able to provide reliable message text for execute/checkpoint failures.

`minigraf_open` returns null on failure but provides no error handle from which to retrieve the reason. `MinigrafOpenException` must therefore be honest when upstream did not expose a detailed native diagnostic.

### 13.2 Structured errors are a native-boundary improvement

Minigraf core has structured error categories. A future C ABI should ideally expose category and stable error code rather than forcing the wrapper to parse human-readable messages.

MinigrafSharp must never parse English error text to infer correctness-critical behavior.

If structured errors become available, map them directly and preserve the original upstream code/category on the .NET exception.

## 14. Native ownership

### 14.1 Database handle

The native database pointer is always owned by an internal `SafeHandle` whose release path calls `minigraf_close` exactly once.

The public API never exposes `IntPtr`, raw pointers or the SafeHandle.

### 14.2 Execute result strings

Every non-null pointer returned by `minigraf_execute` is owned by the caller and must be released with `minigraf_string_free` from the same loaded library.

Required sequence:

```text
call execute
copy UTF-8 bytes/string into managed memory
free native string in finally/SafeHandle
parse/use managed copy
```

If managed JSON parsing fails, the native string must still be freed.

### 14.3 Last-error string

`minigraf_last_error` returns a borrowed pointer. Copy it immediately. Never free it and never retain the pointer.

### 14.4 Disposal and concurrent calls

Interop should pass `SafeHandle` directly where source-generated marshalling can keep the handle alive for the duration of a call.

Dispose must be deterministic and idempotent.

Use after disposal must fail predictably with `ObjectDisposedException` rather than reaching a stale native pointer.

## 15. Concurrency

The current `minigraf-c` handle wraps the Minigraf database in a Rust `Mutex`, so native operations on one handle are serialized by the binding.

MinigrafSharp must preserve that behavior and not advertise parallel execution on a single database handle.

The managed layer should not add a second hidden global lock unless a concrete native safety issue requires it.

Concurrency tests must include:

- multiple callers executing against one in-memory handle;
- dispose racing with an active call;
- repeated open/execute/dispose cycles;
- independent handles where the engine/filesystem semantics permit them;
- file-lock refusal when a second writer/open is forbidden upstream.

Document the proven behavior from tests rather than inferring more than the native engine guarantees.

## 16. Synchronous API

The Minigraf C API is synchronous. MinigrafSharp M1 is synchronous.

Do not implement `ExecuteAsync` by wrapping native work in `Task.Run` inside the library.

Applications that need scheduling may do so at their own boundary.

A true async/cancellable API should only be added if the native engine gains a meaningful cancellation or asynchronous execution contract.

## 17. Native library loading

Use a deterministic resolver similar in discipline to BiscuitSharp and LatticeDbSharp.

Resolution order:

1. explicit development/diagnostic override, for example `MINIGRAFSHARP_NATIVE_PATH`;
2. the verified RID-specific native asset distributed in the NuGet package.

Do not silently search arbitrary system paths and call that compatibility.

A load failure should report:

- process RID;
- process architecture;
- expected native filename;
- locations probed;
- MinigrafSharp version;
- pinned minigraf-c/core version;
- whether an override was requested.

Diagnostics must not expose unrelated environment variables or secrets.

## 18. Native asset distribution

The official minigraf-c v2.0.5 release publishes:

| Upstream asset | Candidate .NET RID |
| --- | --- |
| Linux x86_64 | `linux-x64` |
| Linux aarch64 | `linux-arm64` |
| macOS universal2 | `osx-x64`, `osx-arm64` |
| Windows x86_64 | `win-x64` |

A candidate RID is not supported merely because upstream publishes a binary.

Each RID becomes qualified only after MinigrafSharp runs its own:

- archive/hash verification;
- required-export verification;
- load test;
- ABI smoke test;
- lifecycle test;
- functional Datalog tests;
- persistence and reopen tests;
- WAL/recovery tests relevant to the pinned engine;
- repeated cleanup tests;
- packaged-consumer test;
- NativeAOT smoke test where applicable.

The first preview may qualify a smaller set such as `win-x64`, `linux-x64` and `osx-arm64`, then add other upstream assets as their own gates pass.

## 19. Asset provenance

Do not commit opaque native binaries without provenance.

Record the upstream release in `native/upstream.json`, including at least:

```text
core version
core commit
minigraf-c version
minigraf-c commit
C header SHA-256
release asset name per RID
release asset SHA-256 per RID
source URL
license identity
```

Archive the exact pinned `minigraf.h` in `native/include/` and hash it.

Staging should produce a manifest beside each native asset containing:

```text
MinigrafSharp version
upstream core version/commit
minigraf-c version/commit
RID
native filename
native SHA-256
header SHA-256
source release asset SHA-256
```

The package verifier must compare the packed native bytes with the staged manifest.

Because the current official C ABI has no version-export function, MinigrafSharp cannot ask a loaded arbitrary library to self-identify fully. The runtime must not pretend otherwise.

For packaged assets, identity comes from the package's verified manifest and hash. For an explicit override, report that the asset is an override and distinguish verified manifest identity from unknown/unverified identity.

If upstream adds a version API, use it and cross-check it against the manifest.

## 20. ABI verification

The current C ABI is intentionally small, but still requires verification.

Archive and review `minigraf.h` on every upgrade.

The ABI probe/test suite must verify:

- expected exports are present;
- pointer width assumptions are not embedded in managed structs;
- C calling convention;
- UTF-8 input behavior;
- null handling promised by upstream;
- database handle ownership;
- execute-string ownership and free function pairing;
- borrowed last-error lifetime;
- integer return type for checkpoint;
- failure behavior for null/invalid operations where safely testable;
- repeated open/close and execute/free cycles.

The current header has no public structs beyond the opaque database handle, which reduces layout risk but does not remove ownership risk.

## 21. Functional conformance tests

Tests must execute the real native engine, not only mocks.

Minimum functional scenarios:

1. open and close an in-memory database;
2. open/create, close and reopen a file database;
3. transact a fact and query it;
4. retract a fact and prove it no longer appears in the current view;
5. `:as-of` query returns the historical view;
6. valid-time query behavior is exercised with a known fixture;
7. recursive Datalog rule query;
8. Unicode entity attributes/values;
9. integer, float, Boolean, string and null result decoding where supported;
10. malformed Datalog fails closed with a managed exception;
11. checkpoint succeeds and reopened data remains available;
12. returned native strings are always freed;
13. repeated lifecycle loop does not grow native allocations unexpectedly;
14. one-handle concurrency behaves according to the official binding;
15. unsupported or corrupt database inputs fail rather than appearing successful.

## 22. Upstream known-issue regression tests

For every known upstream issue that could affect .NET consumers, add one of:

- a regression proving the pinned upstream version is fixed;
- a reproduction test marked/documented as an expected upstream limitation;
- a guard/workaround test when the workaround can be implemented without changing semantics.

For v2.0.5 specifically, record tests around:

- same-transaction multi-valued attributes;
- retract then re-assert in one explicit transaction once explicit transactions become bindable;
- repeated assertions and valid-time windows;
- checkpoint/reopen behavior;
- graph/WAL restore mismatch.

Do not make a red upstream bug green by weakening the assertion. The verification ledger should state exactly what the engine currently does.

When v3.0.0 is released, these become upgrade acceptance tests.

## 23. Cross-binding compatibility

MinigrafSharp should prove that it reads and writes real Minigraf databases rather than a wrapper-specific dialect.

Where practical, use committed fixtures produced by the official Rust or Python implementation and prove:

- Rust/Python writes -> MinigrafSharp reads;
- MinigrafSharp writes -> Rust/Python reads;
- historical queries agree for a fixed fixture;
- file reopen across implementations works for the exact supported upstream format.

Do not require byte-identical database files unless upstream defines that as a contract. Test semantic compatibility.

## 24. Package design

The normal user experience should be:

```bash
dotnet add package MinigrafSharp
```

with no separate Rust installation and no manual native-library download for a qualified RID.

Expected NuGet layout:

```text
lib/net8.0/MinigrafSharp.dll
lib/net10.0/MinigrafSharp.dll
runtimes/linux-x64/native/libminigraf.so
runtimes/linux-arm64/native/libminigraf.so
runtimes/osx-x64/native/libminigraf.dylib
runtimes/osx-arm64/native/libminigraf.dylib
runtimes/win-x64/native/minigraf.dll
```

Only qualified RIDs enter the package.

Unsupported RIDs must fail with an actionable `PlatformNotSupportedException` or native-load exception rather than silently loading an arbitrary system library.

## 25. NativeAOT and trimming

M1 should avoid design decisions that block NativeAOT unnecessarily.

Requirements:

- `LibraryImport` rather than reflection-based interop where practical;
- no assembly scanning;
- no runtime code generation;
- `System.Text.Json` decoding either source-generated or otherwise verified under trimming/AOT;
- dedicated `MinigrafSharp.AotSmoke` sample;
- publish and execute the smoke app on every RID claimed to support NativeAOT.

AOT support is qualified by execution, not by compilation alone.

## 26. Documentation set

Before the first public preview, create and keep current:

- `README.md` - what MinigrafSharp is, installation, one-minute example, support status;
- `docs/architecture.md` - managed/native boundary and ownership;
- `docs/native-boundary.md` - exact C ABI, ownership, loading and asset identity;
- `docs/compatibility.md` - wrapper/core/C-binding matrix and upgrade rules;
- `docs/verification.md` - executed evidence with commit/run identities;
- `docs/public-api.txt` - reviewed public API baseline;
- `ROADMAP.md` - milestone status and deferred capabilities;
- `CHANGELOG.md` - package changes;
- `NOTICE` - upstream attribution and redistributed native components.

Recommended ADRs:

```text
0001-wrap-official-minigraf-c.md
0002-native-asset-provenance.md
0003-result-model-and-raw-json-escape-hatch.md
```

If a custom native extension becomes necessary, add a separate ADR before implementation rather than quietly expanding the native boundary.

## 27. Licensing and attribution

Minigraf and minigraf-c are `MIT OR Apache-2.0`.

Recommended MinigrafSharp license: Apache-2.0, matching the existing BiscuitSharp convention and compatible with redistribution of the upstream dual-licensed components under Apache-2.0 terms.

Before publishing:

- include the Minigraf/minigraf-c license material required for redistributed native assets;
- maintain a `NOTICE` identifying MinigrafSharp as an independent wrapper;
- do not imply endorsement or official project status unless upstream explicitly adopts the binding.

Suggested README wording:

> MinigrafSharp is an independent .NET binding for Minigraf and is not currently an official Project Minigraf package.

## 28. Milestones

### M0 - repository and boundary

Deliver:

- solution/repository scaffolding;
- `net8.0;net10.0` projects;
- Apache-2.0 license and NOTICE;
- exact upstream pin metadata;
- archived/hash-verified `minigraf.h`;
- ADR 0001 for the official C binding boundary;
- initial public API contract;
- native asset fetch/staging scripts;
- one local RID loading the exact v2.0.5 native library;
- `MinigrafDatabase.OpenMemory()` smoke call.

Exit criteria:

- managed solution builds warning-free;
- native asset provenance is reproducible from metadata;
- one real native call succeeds without exposing pointers publicly;
- no product code depends on Penghou concepts.

### M1 - first usable binding

Deliver:

- `Open` and `OpenMemory`;
- deterministic SafeHandle lifecycle;
- `ExecuteRaw`;
- typed `Execute` result model;
- `Checkpoint`;
- exception taxonomy based only on information actually available from the C ABI;
- deterministic native resolver;
- functional and temporal tests;
- persistence/reopen tests;
- concurrency/lifecycle tests;
- QuickStart sample.

Exit criteria:

- real engine tests green on each candidate RID selected for M1;
- native allocations/returned strings have proven ownership and cleanup;
- `:as-of`, valid-time and recursive-query fixtures pass;
- raw and typed results agree;
- known v2.0.5 limitations are documented and regression fixtures exist.

### M2 - distribution gate

Deliver:

- verified native assets per qualified RID;
- NuGet package assembly;
- package-content/hash verifier;
- clean external consumer test from isolated NuGet cache;
- .NET 8 and .NET 10 matrix;
- trimming test;
- NativeAOT publish and execute smoke test;
- public API inventory;
- SourceLink/symbol package if used by the repository standard;
- verification ledger with exact commit and CI run identities.

Exit criteria:

- every advertised RID passes the full matrix;
- package contains no unverified native asset;
- a clean consumer needs only `dotnet add package MinigrafSharp`;
- preview publication is a separate explicit release action.

### M2.5 - hardening

Deliver:

- malformed Datalog and malformed-result tests;
- fault-path ownership tests;
- repeated lifecycle stress;
- loader tamper/missing-asset tests;
- diagnostic privacy review;
- package legal inventory validation;
- upstream known-issue matrix;
- cross-binding fixture tests;
- public API freeze for the first stable line.

Exit criteria:

- no malformed native response becomes successful managed data;
- no error path leaks an owned execute result;
- package and documentation state exactly which upstream defects remain inherited.

### M3 - richer native surface

Prioritize based on real consumer demand and upstream cooperation:

1. prepared queries and typed bind values;
2. explicit write transactions;
3. open options;
4. structured error category/code;
5. typed query values that preserve Ref/Keyword identity;
6. native version/ABI identity function;
7. verification/rebuild APIs introduced by newer Minigraf releases.

Prefer upstream `minigraf-c` additions. If a MinigrafSharp-owned extension is required, design and approve its ABI in a new ADR first.

### M4 - agent-memory conveniences, optional

Only after the generic wrapper is stable, consider a separate package or clearly separate layer for agent-oriented helpers such as:

- fact conventions for source/provenance;
- helpers for recording belief revisions;
- typed temporal query helpers;
- chat-memory examples;
- LatticeDB + Minigraf composition examples.

These must not contaminate the generic database binding.

## 29. CI matrix

Target release matrix after qualification:

```text
managed unit tests
native-header/export verification
native integration tests per RID
persistence/recovery tests per RID
net8.0 tests per RID
net10.0 tests per RID
NativeAOT smoke per qualified RID
pack
package archive verification
isolated NuGet consumer per TFM/RID
```

Native tests should run against the same asset that is subsequently packed.

Do not compile one native binary for tests and ship another unverified binary.

## 30. Upgrade protocol

For every upstream Minigraf or minigraf-c upgrade:

1. read the core and C-binding release notes and known issues;
2. verify the core tag/commit and minigraf-c tag/commit;
3. diff the pinned `minigraf.h`;
4. update `native/upstream.json` and archive the new header;
5. verify official asset hashes and provenance;
6. rerun ABI/export tests;
7. rerun functional Datalog and temporal fixtures;
8. rerun persisted-file compatibility tests using databases created by the previous supported version;
9. rerun known-issue regressions;
10. gate each RID independently;
11. rerun AOT/package/clean-consumer tests;
12. update `docs/compatibility.md`, `docs/verification.md` and `CHANGELOG.md`;
13. version the managed package according to public API/behavior impact rather than copying the upstream version number.

File-format compatibility is an upstream storage contract and must be checked independently from C ABI compatibility.

## 31. v3.0.0 upgrade gate

Because Minigraf v3 is expected to change the file format to fix known v2.x integrity/recovery issues, the first v3 upgrade deserves a dedicated gate.

Required checks include:

- open/upgrade databases created by the pinned v2 release;
- confirm documented index rebuild/repair behavior;
- verify same-transaction multi-valued facts;
- verify retract then re-assert semantics;
- verify valid-time window replacement rules;
- verify WAL generation mismatch refusal;
- verify new `verify()` / `rebuild_indexes()` capabilities if they are exposed through the binding;
- determine whether upgraded files can still be opened by v2, and document the answer rather than assuming backward compatibility;
- ensure MinigrafSharp never performs an implicit destructive migration not already defined by upstream.

Do not call v3 support complete because the library loads. Storage upgrade behavior must be exercised with real files and restarts.

## 32. Security and robustness principles

Although MinigrafSharp is not primarily a security library, the native boundary is memory-unsafe from the managed caller's point of view and must be treated rigorously.

Rules:

- no raw pointers in public APIs;
- every owned pointer has one release path;
- no double free;
- borrowed error pointers are copied immediately;
- invalid UTF-8/native output fails closed;
- malformed JSON/native results fail with `MinigrafResultException` rather than partial success;
- untrusted Datalog should not be built through string interpolation;
- no automatic native download at runtime;
- no arbitrary PATH probing;
- explicit native overrides are diagnostic/development features and clearly reported;
- wrapper diagnostics must not include database contents unless explicitly requested by the consumer;
- tests must exercise disposal and error paths, not only the happy path.

## 33. Public-quality bar

MinigrafSharp should be treated as a real ecosystem package, not a private Penghou utility that happens to be public.

Before calling the first preview useful, a .NET developer who has never seen Penghou should be able to:

```bash
dotnet new console
dotnet add package MinigrafSharp
```

then write:

```csharp
using MinigrafSharp;

using var db = MinigrafDatabase.Open("memory.graph");

db.Execute("""
    (transact [[:alice :person/name "Alice"]
               [:alice :friend :bob]
               [:bob :person/name "Bob"]])
    """);

var result = (MinigrafQueryResult)db.Execute("""
    (query [:find ?name
            :where [:alice :friend ?friend]
                   [?friend :person/name ?name]])
    """);

Console.WriteLine(result.Rows[0]["?name"].GetString());
```

and have it work without installing Rust, configuring a server or manually finding a native library.

That is the M1/M2 product test.

## 34. Immediate implementation order

Start in this order:

1. scaffold solution, projects, build settings and tests;
2. add license/NOTICE and upstream metadata;
3. archive the exact v2.0.5 `minigraf.h` and verify its hash;
4. implement native asset staging for one RID, preferably the developer's current platform;
5. implement deterministic native loading;
6. add `SafeMinigrafDatabaseHandle` and `NativeMethods` using `LibraryImport`;
7. prove open-memory/close;
8. prove execute/copy/free with raw JSON;
9. add typed result decoding;
10. add errors and checkpoint;
11. add temporal, persistence and concurrency tests;
12. expand RID qualification;
13. add package/AOT/clean-consumer gates;
14. only then consider prepared queries or a richer native boundary.

Do not begin with agent-memory abstractions. First make the generic binding boring, correct, portable and independently useful.
