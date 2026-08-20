use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
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

impl PersistentState {
    pub async fn open(root: impl Into<PathBuf>) -> NativeResult<Self> {
        let root = root.into();
        fs::create_dir_all(root.join(AUTHORS_DIRECTORY)).await?;
        let state = Self { root };
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
}
