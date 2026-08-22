use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use iroh::SecretKey;
use serde::{Deserialize, Serialize};
use tokio::fs;

use crate::{
    document::HlcWire,
    error::{ErrorCode, NativeError, NativeResult},
};

const MANIFEST_FILE: &str = "native-state.json";
const ENDPOINT_SECRET_FILE: &str = "endpoint-secret.bin";
const AUTHORS_DIRECTORY: &str = "authors";
const BACKEND_KIND: &str = "iroh-docs";
const BACKEND_STORAGE_VERSION: u16 = 1;
const PROTOCOL_VERSION: u16 = 1;
const MAX_MANIFEST_BYTES: u64 = 16 * 1024 * 1024;
const MAX_MANIFEST_REVISIONS: usize = 1_000_000;
const MAX_CHARACTER_BYTES: usize = 4096;
const MAX_TICKET_BYTES: usize = 64 * 1024;
const MAX_TEMP_FILES_PER_CLEANUP: usize = 512;
const RUNTIME_DIAGNOSTIC_FILE: &str = "native-error.json";

#[derive(Debug, Default, Serialize, Deserialize)]
struct Manifest {
    #[serde(default = "default_protocol_version")]
    protocol_version: u16,
    #[serde(default = "default_backend_kind")]
    backend_kind: String,
    #[serde(default = "default_backend_storage_version")]
    backend_storage_version: u16,
    #[serde(default)]
    group_namespace_id: Option<[u8; 32]>,
    #[serde(default)]
    group_ticket: Option<Vec<u8>>,
    selected_character: Option<Vec<u8>>,
    last_hlc: Option<HlcWire>,
    revisions: BTreeMap<String, u64>,
}

fn default_protocol_version() -> u16 {
    PROTOCOL_VERSION
}
fn default_backend_kind() -> String {
    BACKEND_KIND.to_owned()
}
fn default_backend_storage_version() -> u16 {
    BACKEND_STORAGE_VERSION
}

/// Persistent native identity and monotonic register state.
#[derive(Debug, Clone)]
pub struct PersistentState {
    root: PathBuf,
}

/// Bounded cleanup result for crash leftovers produced by [`atomic_write`].
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct StorageCleanupReport {
    pub removed_files: u32,
    pub reclaimed_bytes: u64,
}

#[derive(Debug, Serialize)]
struct RuntimeDiagnostic<'a> {
    unix_ms: u128,
    error_code: u32,
    error_id: u64,
    category: &'a str,
}

impl PersistentState {
    pub async fn open(root: impl Into<PathBuf>) -> NativeResult<Self> {
        let root = root.into();
        fs::create_dir_all(root.join(AUTHORS_DIRECTORY)).await?;
        let state = Self { root };
        state
            .cleanup_temporary_files(Duration::from_secs(24 * 60 * 60))
            .await?;
        match fs::metadata(state.manifest_path()).await {
            Ok(_) => {
                let manifest = state.read_manifest().await?;
                if manifest.protocol_version != PROTOCOL_VERSION
                    || manifest.backend_kind != BACKEND_KIND
                    || manifest.backend_storage_version != BACKEND_STORAGE_VERSION
                {
                    return Err(NativeError::new(
                        ErrorCode::Storage,
                        "persistent mesh backend or protocol version is incompatible",
                    ));
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                state
                    .write_manifest(&Manifest {
                        protocol_version: PROTOCOL_VERSION,
                        backend_kind: BACKEND_KIND.to_owned(),
                        backend_storage_version: BACKEND_STORAGE_VERSION,
                        ..Manifest::default()
                    })
                    .await?;
            }
            Err(error) => return Err(error.into()),
        }
        Ok(state)
    }

    /// Remove only stale temporary files created by this module's atomic writes.
    ///
    /// Current manifests, endpoint secrets, author keys, tickets, and backend directories are
    /// deliberately outside this set. A cleanup failure is returned to the caller so it remains
    /// visible rather than silently risking unbounded crash-leftover growth.
    pub async fn cleanup_temporary_files(
        &self,
        max_age: Duration,
    ) -> NativeResult<StorageCleanupReport> {
        let now = SystemTime::now();
        let directories = [self.root.clone(), self.root.join(AUTHORS_DIRECTORY)];
        let mut report = StorageCleanupReport::default();
        let mut inspected = 0usize;
        for directory in directories {
            let mut entries = fs::read_dir(&directory).await?;
            while let Some(entry) = entries.next_entry().await? {
                inspected = inspected.saturating_add(1);
                if inspected > MAX_TEMP_FILES_PER_CLEANUP {
                    return Err(NativeError::new(
                        ErrorCode::LimitExceeded,
                        "persistent cleanup found too many directory entries",
                    ));
                }
                let file_type = entry.file_type().await?;
                if !file_type.is_file() || !is_atomic_temp_name(&directory, entry.file_name()) {
                    continue;
                }
                let metadata = entry.metadata().await?;
                let age = now
                    .duration_since(metadata.modified().unwrap_or(now))
                    .unwrap_or_default();
                if age < max_age {
                    continue;
                }
                fs::remove_file(entry.path()).await.map_err(|error| {
                    NativeError::new(
                        ErrorCode::Storage,
                        format!("cannot remove stale native temporary file: {error}"),
                    )
                })?;
                report.removed_files = report.removed_files.saturating_add(1);
                report.reclaimed_bytes = report.reclaimed_bytes.saturating_add(metadata.len());
            }
        }
        Ok(report)
    }

    /// Persist one bounded, secret-free supervisor/command diagnostic for restart inspection.
    /// Payloads, tickets, endpoint keys, error messages, and record contents are never included.
    pub(crate) async fn write_runtime_diagnostic(
        root: impl AsRef<Path>,
        error_code: u32,
        error_id: u64,
    ) -> NativeResult<()> {
        let diagnostic = RuntimeDiagnostic {
            unix_ms: SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_millis(),
            error_code,
            error_id,
            category: diagnostic_category(error_code),
        };
        let bytes = serde_json::to_vec(&diagnostic).map_err(|error| {
            NativeError::new(
                ErrorCode::Storage,
                format!("cannot encode native runtime diagnostic: {error}"),
            )
        })?;
        let root = root.as_ref();
        fs::create_dir_all(root).await?;
        atomic_write(&root.join(RUNTIME_DIAGNOSTIC_FILE), &bytes).await
    }

    fn manifest_path(&self) -> PathBuf {
        self.root.join(MANIFEST_FILE)
    }

    fn endpoint_secret_path(&self) -> PathBuf {
        self.root.join(ENDPOINT_SECRET_FILE)
    }

    fn author_path(&self, character: &[u8]) -> PathBuf {
        let digest = blake3::hash(character);
        self.root
            .join(AUTHORS_DIRECTORY)
            .join(format!("{}.bin", hex::encode(digest.as_bytes())))
    }

    async fn read_manifest(&self) -> NativeResult<Manifest> {
        let metadata = fs::metadata(self.manifest_path()).await?;
        if metadata.len() > MAX_MANIFEST_BYTES {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "persistent mesh manifest exceeds native limit",
            ));
        }
        let bytes = fs::read(self.manifest_path()).await?;
        let manifest: Manifest = serde_json::from_slice(&bytes).map_err(|error| {
            NativeError::new(
                ErrorCode::Storage,
                format!("native state is corrupt: {error}"),
            )
        })?;
        if manifest.revisions.len() > MAX_MANIFEST_REVISIONS
            || manifest
                .selected_character
                .as_ref()
                .is_some_and(|character| character.len() > MAX_CHARACTER_BYTES)
            || manifest
                .group_ticket
                .as_ref()
                .is_some_and(|ticket| ticket.len() > MAX_TICKET_BYTES)
        {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "persistent mesh manifest exceeds native limits",
            ));
        }
        Ok(manifest)
    }

    async fn write_manifest(&self, manifest: &Manifest) -> NativeResult<()> {
        let bytes = serde_json::to_vec(manifest).map_err(|error| {
            NativeError::new(
                ErrorCode::Storage,
                format!("cannot encode native state: {error}"),
            )
        })?;
        if bytes.len() as u64 > MAX_MANIFEST_BYTES {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "persistent mesh manifest exceeds native limit",
            ));
        }
        atomic_write(&self.manifest_path(), &bytes).await
    }

    pub async fn endpoint_secret(&self) -> NativeResult<[u8; 32]> {
        let path = self.endpoint_secret_path();
        match fs::read(&path).await {
            Ok(bytes) => <[u8; 32]>::try_from(bytes.as_slice()).map_err(|_| {
                NativeError::new(
                    ErrorCode::Storage,
                    "persisted endpoint secret has invalid length",
                )
            }),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                let bytes = SecretKey::generate().to_bytes();
                atomic_write(&path, &bytes).await?;
                Ok(bytes)
            }
            Err(error) => Err(error.into()),
        }
    }

    pub async fn selected_character(&self) -> NativeResult<Option<Vec<u8>>> {
        Ok(self.read_manifest().await?.selected_character)
    }

    pub async fn set_selected_character(&self, character: Option<Vec<u8>>) -> NativeResult<()> {
        let mut manifest = self.read_manifest().await?;
        manifest.selected_character = character;
        self.write_manifest(&manifest).await
    }

    pub async fn set_group_namespace(&self, namespace: Option<[u8; 32]>) -> NativeResult<()> {
        let mut manifest = self.read_manifest().await?;
        manifest.group_namespace_id = namespace;
        self.write_manifest(&manifest).await
    }

    pub async fn group_namespace(&self) -> NativeResult<Option<[u8; 32]>> {
        Ok(self.read_manifest().await?.group_namespace_id)
    }

    pub async fn group_ticket(&self) -> NativeResult<Option<Vec<u8>>> {
        Ok(self.read_manifest().await?.group_ticket)
    }

    pub async fn set_group_ticket(&self, ticket: Option<Vec<u8>>) -> NativeResult<()> {
        if ticket
            .as_ref()
            .is_some_and(|ticket| ticket.len() > MAX_TICKET_BYTES)
        {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "group ticket exceeds native limit",
            ));
        }
        let mut manifest = self.read_manifest().await?;
        manifest.group_ticket = ticket;
        self.write_manifest(&manifest).await
    }

    pub async fn last_hlc(&self) -> NativeResult<Option<HlcWire>> {
        Ok(self.read_manifest().await?.last_hlc)
    }

    pub async fn save_hlc(&self, hlc: HlcWire) -> NativeResult<()> {
        let mut manifest = self.read_manifest().await?;
        manifest.last_hlc = Some(hlc);
        self.write_manifest(&manifest).await
    }

    pub async fn load_or_create_character_author(
        &self,
        character: &[u8],
    ) -> NativeResult<[u8; 32]> {
        if character.is_empty() {
            return Err(NativeError::invalid("character author key cannot be empty"));
        }
        let path = self.author_path(character);
        match fs::read(&path).await {
            Ok(bytes) => <[u8; 32]>::try_from(bytes.as_slice()).map_err(|_| {
                NativeError::new(
                    ErrorCode::Storage,
                    "persisted character author has invalid length",
                )
            }),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                let bytes = SecretKey::generate().to_bytes();
                atomic_write(&path, &bytes).await?;
                Ok(bytes)
            }
            Err(error) => Err(error.into()),
        }
    }

    pub async fn reserve_revision(&self, key: &[u8], observed: u64) -> NativeResult<u64> {
        let key = hex::encode(key);
        let mut manifest = self.read_manifest().await?;
        let previous = manifest.revisions.get(&key).copied().unwrap_or_default();
        let base = previous.max(observed);
        let next = base.checked_add(1).ok_or_else(|| {
            NativeError::new(ErrorCode::LimitExceeded, "author revision exhausted")
        })?;
        manifest.revisions.insert(key, next);
        self.write_manifest(&manifest).await?;
        Ok(next)
    }

    pub async fn remember_revision(&self, key: &[u8], revision: u64) -> NativeResult<()> {
        let key = hex::encode(key);
        let mut manifest = self.read_manifest().await?;
        let current = manifest.revisions.get(&key).copied().unwrap_or_default();
        if revision > current {
            manifest.revisions.insert(key, revision);
            self.write_manifest(&manifest).await?;
        }
        Ok(())
    }
}

fn diagnostic_category(error_code: u32) -> &'static str {
    match error_code {
        1 => "invalid-argument",
        2 => "abi-mismatch",
        3 => "invalid-handle",
        4 => "invalid-state",
        5 => "queue-full",
        6 => "no-event",
        7 => "not-ready",
        8 => "closing",
        9 => "already-destroyed",
        10 => "not-found",
        11 => "limit-exceeded",
        12 => "invalid-record",
        13 => "clock-drift",
        14 => "storage",
        15 => "network",
        16 => "panic",
        17 => "internal",
        18 => "snapshot-stale",
        _ => "unknown",
    }
}

fn is_atomic_temp_name(directory: &Path, name: std::ffi::OsString) -> bool {
    let Some(name) = name.to_str() else {
        return false;
    };
    if name.starts_with("native-state.tmp-") || name.starts_with("endpoint-secret.tmp-") {
        return true;
    }
    if directory.file_name().and_then(|value| value.to_str()) != Some(AUTHORS_DIRECTORY) {
        return false;
    }
    let Some((prefix, suffix)) = name.split_once(".tmp-") else {
        return false;
    };
    prefix.len() == 64
        && prefix.bytes().all(|byte| byte.is_ascii_hexdigit())
        && !suffix.is_empty()
        && suffix
            .bytes()
            .all(|byte| byte.is_ascii_digit() || byte == b'-')
}

async fn atomic_write(path: &Path, bytes: &[u8]) -> NativeResult<()> {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let temp = path.with_extension(format!("tmp-{}-{}", std::process::id(), stamp));
    fs::write(&temp, bytes).await?;
    if let Err(error) = fs::rename(&temp, path).await {
        if error.kind() != std::io::ErrorKind::AlreadyExists {
            let _ = fs::remove_file(&temp).await;
            return Err(error.into());
        }
        // Windows rename does not replace an existing file. The temporary file still ensures
        // readers never observe a partially written manifest; the brief remove/rename gap is
        // limited to replacement on platforms without atomic rename-overwrite semantics.
        if let Err(remove_error) = fs::remove_file(path).await {
            let _ = fs::remove_file(&temp).await;
            return Err(remove_error.into());
        }
        if let Err(rename_error) = fs::rename(&temp, path).await {
            let _ = fs::remove_file(&temp).await;
            return Err(rename_error.into());
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn group_identity_ticket_and_hlc_high_water_survive_reopen() {
        let root = std::env::temp_dir().join(format!(
            "gathermesh-persistence-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        let state = PersistentState::open(&root)
            .await
            .expect("persistent state should open");
        let namespace = [0x19; 32];
        let ticket = b"opaque-ticket-state".to_vec();
        let hlc = HlcWire {
            physical_unix_ms: 100,
            logical: 200,
            node_id: [0x29; 16],
            raw: 200,
        };
        state
            .set_group_namespace(Some(namespace))
            .await
            .expect("namespace should persist");
        state
            .set_group_ticket(Some(ticket.clone()))
            .await
            .expect("ticket should persist");
        state.save_hlc(hlc).await.expect("HLC should persist");
        let key = b"v1/workers/high-water";
        state
            .remember_revision(key, 7)
            .await
            .expect("revision high-water should persist");

        let reopened = PersistentState::open(&root)
            .await
            .expect("persistent state should reopen");
        assert_eq!(
            reopened.group_namespace().await.expect("namespace read"),
            Some(namespace)
        );
        assert_eq!(
            reopened.group_ticket().await.expect("ticket read"),
            Some(ticket)
        );
        assert_eq!(reopened.last_hlc().await.expect("HLC read"), Some(hlc));
        assert_eq!(
            reopened
                .reserve_revision(key, 7)
                .await
                .expect("reopened revision high-water should advance"),
            8
        );
        tokio::fs::remove_dir_all(root)
            .await
            .expect("test persistence directory should be removable");
    }

    #[tokio::test]
    async fn cleanup_removes_only_owned_atomic_write_leftovers() {
        let root = std::env::temp_dir().join(format!(
            "gathermesh-persistence-cleanup-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        let state = PersistentState::open(&root)
            .await
            .expect("persistent state should open");
        let owned_temp = root.join("native-state.tmp-test-1");
        let unrelated_temp = root.join("foreign.tmp-test-1");
        tokio::fs::write(&owned_temp, b"crash-leftover")
            .await
            .expect("owned temporary file should be writable");
        tokio::fs::write(&unrelated_temp, b"unrelated")
            .await
            .expect("unrelated temporary file should be writable");

        let report = state
            .cleanup_temporary_files(Duration::ZERO)
            .await
            .expect("cleanup should report owned leftovers");
        assert_eq!(report.removed_files, 1);
        assert_eq!(report.reclaimed_bytes, b"crash-leftover".len() as u64);
        assert!(!owned_temp.exists());
        assert!(unrelated_temp.exists());
        assert!(root.join(MANIFEST_FILE).exists());

        tokio::fs::remove_dir_all(root)
            .await
            .expect("test persistence directory should be removable");
    }

    #[tokio::test]
    async fn runtime_diagnostic_is_bounded_and_excludes_record_secrets() {
        let root = std::env::temp_dir().join(format!(
            "gathermesh-persistence-diagnostic-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        PersistentState::open(&root)
            .await
            .expect("persistent state should open");
        let secret = "ticket=SECRET-123 payload=secret-payload endpoint=https://secret.example";
        PersistentState::write_runtime_diagnostic(&root, 15, 9)
            .await
            .expect("runtime diagnostic should persist");
        let bytes = tokio::fs::read(root.join(RUNTIME_DIAGNOSTIC_FILE))
            .await
            .expect("runtime diagnostic should be readable");
        let value: serde_json::Value =
            serde_json::from_slice(&bytes).expect("runtime diagnostic should be JSON");
        assert_eq!(value["error_code"], 15);
        assert_eq!(value["error_id"], 9);
        assert_eq!(value["category"], "network");
        assert!(!String::from_utf8_lossy(&bytes).contains(secret));
        assert!(!String::from_utf8_lossy(&bytes).contains("SECRET-123"));

        tokio::fs::remove_dir_all(root)
            .await
            .expect("test persistence directory should be removable");
    }
}
