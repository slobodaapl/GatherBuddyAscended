# GatherMesh native runtime

GatherMesh is the Rust/Iroh transport and persistence boundary used by the FC mesh. C# owns
world projection and fulfillment policy; this library owns authenticated current-register
records, HLC stamping, durable identity, bounded events/snapshots, and the ABI v2 lifecycle.

## Wire and compatibility contract

Every application record is a signed, author-owned absolute register value. The author revision
orders updates from one author; a relayed record keeps its original author, revision, HLC, payload,
signature, and canonical compact JSON bytes. Accepted remote HLC timestamps advance the local HLC
before a later local record is authored. Equal-revision forks are retained as deterministic
quarantine evidence and excluded from actionable projection; neither arrival order nor HLC picks a
fork winner.

Protocol, record schema, planner semantics, and loaded game version are compatibility inputs.
The canonical envelope rejects every undeclared envelope member and every unsupported protocol
major; envelope bytes are never silently reserialized. The public-list payload has its own forward-
compatible rule: unknown optional payload members are ignored for display/projection, while unknown
required semantics (including a newer planner-semantics version) remain displayable but are
non-executable with a visible reason. The native ABI exposes statuses and owned buffers;
Rust unwinds, C# exceptions, and C++ exceptions never cross the boundary. Abort/OOM/stack-overflow,
undefined behavior, and an external native fault are outside the in-process recovery guarantee.

Native Iroh verification remains authoritative for transport admission. The managed envelope
decoder independently verifies Ed25519 over the exact Rust postcard signing bytes and BLAKE3 over
the exact received canonical envelope, compares the latter with native event contentHash, and
retains the existing payload SHA-256 check. Both language gates use the checked-in signing-byte
fixture; any mutation, key mismatch, signature mismatch, or hash mismatch fails closed with a
bounded non-secret reason. The managed verifier is Bouncy Castle 2.7.0; its attribution and
license are recorded in [`NOTICE.md`](NOTICE.md).

## Durable storage and cleanup

The configured `config/fcmesh` directory contains the native manifest, endpoint secret, per-
character author keys, Iroh `blobs`, and Iroh `docs` backend data. Manifest writes use a temporary
file followed by rename. Startup removes only stale temporary files created by those writes, with a
bounded scan; current manifests, author keys, endpoint secrets, tickets, backend markers, blobs,
and documents are never removed by this cleanup. The latest bounded supervisor/command diagnostic
is written to `native-error.json` without payloads, tickets, endpoint keys, or record contents.
Cleanup errors fail startup visibly. An incompatible
protocol/backend store is rejected; it is not converted or deleted automatically.

The blob backend runs its own periodic mark/sweep collector. Current Iroh tags, temporary roots,
and the docs protection callback for every live document value are liveness roots, so active records
remain protected. The native integration test invokes the real docs protection callback against a
live record and proves its blob is returned as protected; the pinned Iroh version exposes no public
manual GC trigger, so that test does not claim a forced sweep. Event coalescing and one current
register per logical key limit application-side churn. Back up the directory before manual repair;
do not delete `native-state.json`, `endpoint-secret.bin`, `authors`, `blobs`, or `docs` while the
plugin is running.

## Group, relay, and recovery operation

The creator publishes an opaque invite ticket. A joiner must reach a peer and complete initial
sync before its state is considered ready. Possession of a ticket is the v1 membership authority;
there is no member-revocation protocol. Relay mode and custom relay URLs are configurable. Relay
traffic is encrypted between Iroh endpoints; relay loss pauses connectivity and retries through the
native service rather than authoring substitute state.

Native startup restores the durable endpoint, character author, group identity, HLC high-water,
and register revisions. Corrupt or incomplete durable state fails closed with a bounded diagnostic.
Managed recovery rebuilds a world snapshot from absolute records and must re-run current capability
and craft admission before any next FC action. A persisted FC craft never becomes a private craft
because recovery data is stale or incomplete.

## FC physical semantics

`FcInventoryTransferRecord` is one atomic physical observation: complete `ChestAfter` and
`WorkerAfter` are committed together or neither is projected. Withdrawal and deposit are explicit
travel/interaction sequences. Stop or cancel does not teleport held items into the chest and does
not schedule a deposit; only an explicit deposit action may do so.

The FC chest in the estate is one shared registrable location. Default anchors are approximate
environment hints for vnavmesh; the runtime resolves the live object identity before interaction and
does not treat coordinates or hardcoded object IDs as authoritative. Chest pages, permissions,
stack behavior, full slots, and patch-specific UI remain live FFXIV acceptance work.

## Native process harness and soak

`gathermesh-node` is a checked-in separate-process exercise of create/join, canonical record put,
restart, transitively connected peers, and bounded payloads. The explicit long-running harness is
the ignored `soak_process` test. A bounded smoke can be run with
`just gathermesh-soak 15 1 0`; `relay_mode=1` disables relay, `relay_mode=0` uses the
configured Iroh default, and `relay_mode=2` forces relay-only address filtering so direct IP
candidates are excluded. For the forced-relay smoke use `just gathermesh-soak 15 1 2`; an optional
fourth positional argument supplies an externally managed custom relay URL, for example
`just gathermesh-soak 15 1 2 https://...`. Successful same-host
convergence then demonstrates that direct candidates were filtered and traffic used the reachable
external relay. A direct/loopback run is not relay evidence; proving relay traversal requires that
external relay environment. The harness records its seed and round count and fails on process or
reconciliation errors. No native process harness is a live FFXIV or Donatello quality oracle.

## Build and release

The Rust crate is pinned through `gathermesh/Cargo.lock` and is built as `gathermesh_ffi.dll` for
Windows. Release CI checks the x64 PE architecture and required ABI exports, keeps symbols as a
separate required artifact, downloads the DLL into the cross-target path expected by
`GatherBuddy.csproj`, verifies it beside the plugin, and verifies it in the release archive. The
`just test` recipe builds and places `libgathermesh_ffi.so` before the real managed P/Invoke smoke;
the release plugin still uses the Windows DLL. `just publish-local` rebuilds the GatherMesh and
Donatello Windows DLLs and places both beside the Release plugin after the final gate. Rust source
and transitive dependency license notices are in [`NOTICE.md`](NOTICE.md).

The repository includes `tests/cpp_consumer_smoke.cpp`; CI compiles and runs it against the native
library, including a throwing C++ dependency adapter before the first C ABI call. The same native
gate inspects the Linux symbol table for every fixed ABI export and rejects reverse-callback or
managed-facing symbols. This does not cover C++ abort/OOM/stack-overflow, undefined behavior, or
external native faults.
