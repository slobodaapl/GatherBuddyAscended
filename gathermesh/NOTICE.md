# GatherMesh native dependency notices

GatherMesh is distributed under the repository Apache License 2.0. The Rust native crate links
the pinned dependencies listed in `Cargo.lock`; their terms remain applicable to source and binary
redistribution. This notice identifies the native dependency families and their upstream license
sources. The exact locked versions and license metadata are authoritative for a release.

| Dependency family | Upstream license source |
| --- | --- |
| `iroh`, `iroh-blobs`, `iroh-docs`, `iroh-gossip` | [n0-computer/iroh](https://github.com/n0-computer/iroh) and each crate's `LICENSE`/`NOTICE` files |
| `uhlc` | [uhlc crate](https://crates.io/crates/uhlc) and its repository license file |
| `tokio` | [tokio-rs/tokio](https://github.com/tokio-rs/tokio/blob/master/LICENSE) |
| `serde`, `serde_json` | [serde-rs/serde](https://github.com/serde-rs/serde/blob/master/LICENSE-APACHE) and the package license files |
| `bytes`, `futures-util`, `anyhow`, `base64`, `hex`, `postcard`, `sha2` | The corresponding package license files recorded by Cargo for the locked release |
| `blake3` | [BLAKE3 licensing](https://github.com/BLAKE3-team/BLAKE3/tree/master) and the package license files |
| `BouncyCastle.Cryptography` 2.7.0 | [Bouncy Castle license](https://www.bouncycastle.org/licence.html); the managed package is distributed under its MIT-based Bouncy Castle license |

The native release workflow retains the source checkout and uploads symbols separately from the
DLL. A redistributor must carry this notice, the repository `LICENSE`, and the applicable upstream
notices with the packaged plugin. No Iroh service or relay grants ownership of application records;
relay infrastructure is an encrypted transport dependency only.

GatherMesh's native boundary remains the transport authority, while managed code independently
verifies Ed25519 over the fixed Rust postcard signing bytes and BLAKE3 over exact canonical
envelope bytes, comparing native event hashes in constant time. The pinned Bouncy Castle package
provides those algorithms; its package license must remain with the plugin distribution. The
pinned Iroh blob collector exposes periodic GC and a live-document protection callback; it does
not expose a public forced-sweep operation used by the repository tests.
