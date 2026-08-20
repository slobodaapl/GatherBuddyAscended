use std::{
    collections::{BTreeMap, HashMap},
    path::PathBuf,
    time::Duration,
};

use bytes::Bytes;
use futures_util::FutureExt;
use iroh::{Endpoint, RelayMode, RelayUrl, Watcher, endpoint::presets, protocol::Router};
use iroh_blobs::{ALPN as BLOBS_ALPN, BlobsProtocol, store::fs::FsStore};
use iroh_docs::{
    ALPN as DOCS_ALPN, Author, AuthorId, DocTicket, Entry,
    api::{
        Doc,
        protocol::{AddrInfoOptions, ShareMode},
    },
    engine::LiveEvent,
    protocol::Docs,
    store::Query,
};
use iroh_gossip::{ALPN as GOSSIP_ALPN, net::Gossip};
use n0_future::StreamExt as N0StreamExt;
use serde::{Deserialize, Serialize};
use tokio::sync::mpsc;
use uhlc::{HLC, HLCBuilder, ID, Timestamp};

use crate::{
    document::{
        HlcWire, MAX_RECORD_TYPE_BYTES, MeshEnvelope, MeshEnvelopeInput, RECORD_ID_BYTES,
        parse_record_id, secret_key_from_bytes, validate_record_id_bytes,
        validate_record_id_for_key,
    },
    error::{ErrorCode, NativeError, NativeResult},
    events::{EventKind, NativeEvent, SnapshotRecord},
    persistence::PersistentState,
};

pub const RECORD_TYPE_GROUP_METADATA: &str = "group-metadata";
pub const RECORD_TYPE_PUBLISHED_LIST: &str = "published-list";
pub const RECORD_TYPE_WORKER_SESSION: &str = "worker-session";
pub const RECORD_TYPE_CHEST_SNAPSHOT: &str = "chest-snapshot";
pub const RECORD_TYPE_CAPABILITY_REQUEST: &str = "capability-request";
pub const RECORD_TYPE_CAPABILITY_RESPONSE: &str = "capability-response";
pub const RECORD_TYPE_INVENTORY_TRANSFER: &str = "inventory-transfer";

#[derive(Debug, Clone)]
pub struct NativeConfig {
    pub storage_directory: PathBuf,
    pub event_capacity: usize,
    pub command_capacity: usize,
    pub max_key_bytes: usize,
    pub max_value_bytes: usize,
    pub relay_mode: u32,
    pub relay_urls: Vec<String>,
    pub max_hlc_delta_ms: u64,
    pub max_snapshot_records: usize,
    pub max_snapshot_lifetime: Duration,
}

impl Default for NativeConfig {
    fn default() -> Self {
        Self {
            storage_directory: PathBuf::from("config/fcmesh"),
            event_capacity: 1024,
            command_capacity: 256,
            max_key_bytes: 4096,
            max_value_bytes: 4 * 1024 * 1024,
            relay_mode: 0,
            relay_urls: Vec::new(),
            max_hlc_delta_ms: 300_000,
            max_snapshot_records: 100_000,
            max_snapshot_lifetime: Duration::from_secs(60),
        }
    }
}

#[derive(Debug)]
pub enum LiveInput {
    Event(Result<LiveEvent, String>),
    PathChanged,
}

#[derive(Debug, Serialize, Deserialize)]
struct GroupMetadata {
    protocol_version: u16,
    backend_kind: String,
    backend_storage_version: u16,
    namespace: [u8; 32],
}

fn is_canonical_guid(segment: &str) -> bool {
    parse_record_id(segment).is_ok()
}

fn validate_record_key(key: &[u8], record_type: &str, docs_author: AuthorId) -> NativeResult<()> {
    let key = std::str::from_utf8(key)
        .map_err(|_| NativeError::new(ErrorCode::InvalidRecord, "record key is not UTF-8"))?;
    let segments = key.split('/').collect::<Vec<_>>();
    let (owner, expected_type, guid) = match segments.as_slice() {
        ["v1", "meta", "group", owner] => (*owner, RECORD_TYPE_GROUP_METADATA, false),
        ["v1", "lists", owner, _list_id] => (*owner, RECORD_TYPE_PUBLISHED_LIST, true),
        ["v1", "workers", owner] => (*owner, RECORD_TYPE_WORKER_SESSION, false),
        ["v1", "chest", owner] => (*owner, RECORD_TYPE_CHEST_SNAPSHOT, false),
        ["v1", "capability-requests", owner, _request_id] => {
            (*owner, RECORD_TYPE_CAPABILITY_REQUEST, true)
        }
        ["v1", "capability-responses", owner, _request_id] => {
            (*owner, RECORD_TYPE_CAPABILITY_RESPONSE, true)
        }
        ["v1", "transfers", owner, _operation_id] => (*owner, RECORD_TYPE_INVENTORY_TRANSFER, true),
        _ => {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "unsupported or malformed mesh record key",
            ));
        }
    };
    let expected_owner = hex::encode(docs_author.as_bytes());
    if owner != expected_owner {
        return Err(NativeError::new(
            ErrorCode::InvalidRecord,
            "record key owner does not match authenticated document author",
        ));
    }
    if guid {
        let guid_segment = segments.last().copied().unwrap_or_default();
        if !is_canonical_guid(guid_segment) {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "record key GUID is not canonical",
            ));
        }
    }
    if record_type != expected_type || record_type.len() > MAX_RECORD_TYPE_BYTES {
        return Err(NativeError::new(
            ErrorCode::InvalidRecord,
            "record type does not match its key layout",
        ));
    }
    Ok(())
}

fn record_id_from_namespace(namespace: &[u8; 32]) -> [u8; RECORD_ID_BYTES] {
    let mut record_id = [0u8; RECORD_ID_BYTES];
    record_id.copy_from_slice(&namespace[..RECORD_ID_BYTES]);
    if record_id.iter().all(|byte| *byte == 0) {
        record_id[0] = 1;
    }
    record_id
}

fn validate_incoming_hlc(config: &NativeConfig, timestamp: &Timestamp) -> NativeResult<()> {
    let validation_hlc = HLCBuilder::new()
        .with_max_delta(Duration::from_millis(config.max_hlc_delta_ms))
        .build();
    validation_hlc
        .update_with_timestamp(timestamp)
        .map_err(|error| {
            NativeError::new(
                ErrorCode::ClockDrift,
                format!("remote HLC quarantined: {error}"),
            )
        })
}

#[derive(Debug)]
struct JoinProgress {
    contacted_peer: Option<[u8; 32]>,
    sync_finished: bool,
    compatible_metadata: bool,
    content_ready: bool,
    content_error: bool,
}

pub struct NativeNode {
    config: NativeConfig,
    live_sender: mpsc::Sender<LiveInput>,
    persistence: Option<PersistentState>,
    endpoint: Option<Endpoint>,
    blobs: Option<FsStore>,
    docs: Option<Docs>,
    router: Option<Router>,
    doc: Option<Doc>,
    author: Option<Author>,
    character_key: Option<Vec<u8>>,
    hlc: HLC,
    registers: BTreeMap<Vec<u8>, MeshEnvelope>,
    forks: BTreeMap<Vec<u8>, BTreeMap<Vec<u8>, MeshEnvelope>>,
    pending_entries: HashMap<[u8; 32], Vec<Entry>>,
    join_progress: Option<JoinProgress>,
    initial_sync_emitted: bool,
    group_open: bool,
    joined: bool,
    started: bool,
}

impl NativeNode {
    pub fn new(config: NativeConfig, live_sender: mpsc::Sender<LiveInput>) -> Self {
        Self {
            config,
            live_sender,
            persistence: None,
            endpoint: None,
            blobs: None,
            docs: None,
            router: None,
            doc: None,
            author: None,
            character_key: None,
            hlc: HLCBuilder::new().build(),
            registers: BTreeMap::new(),
            forks: BTreeMap::new(),
            pending_entries: HashMap::new(),
            join_progress: None,
            initial_sync_emitted: false,
            group_open: false,
            joined: false,
            started: false,
        }
    }

    pub fn is_joined(&self) -> bool {
        self.joined
    }

    pub fn endpoint_id(&self) -> Vec<u8> {
        self.endpoint
            .as_ref()
            .map(|endpoint| endpoint.id().as_bytes().to_vec())
            .unwrap_or_default()
    }

    pub fn namespace_id(&self) -> Vec<u8> {
        self.doc
            .as_ref()
            .map(|doc| doc.id().as_bytes().to_vec())
            .unwrap_or_default()
    }

    pub async fn start(&mut self) -> NativeResult<Vec<NativeEvent>> {
        if self.started {
            return Ok(Vec::new());
        }
        let persistence = PersistentState::open(self.config.storage_directory.clone()).await?;
        let selected_before_start = self.character_key.clone();
        self.character_key = selected_before_start.or(persistence.selected_character().await?);
        let endpoint_secret = persistence.endpoint_secret().await?;
        let builder = if !self.config.relay_urls.is_empty() {
            let urls = self
                .config
                .relay_urls
                .iter()
                .map(|url| {
                    url.parse::<RelayUrl>().map_err(|error| {
                        NativeError::new(
                            ErrorCode::InvalidArgument,
                            format!("invalid relay URL: {error}"),
                        )
                    })
                })
                .collect::<NativeResult<Vec<_>>>()?;
            Endpoint::builder(presets::Minimal).relay_mode(RelayMode::custom(urls))
        } else if self.config.relay_mode == 1 {
            Endpoint::builder(presets::N0DisableRelay)
        } else {
            Endpoint::builder(presets::N0)
        };
        let endpoint = builder
            .secret_key(secret_key_from_bytes(&endpoint_secret))
            .bind()
            .await
            .map_err(|error| NativeError::new(ErrorCode::Network, error.to_string()))?;

        let blobs = FsStore::load(self.config.storage_directory.join("blobs"))
            .await
            .map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))?;
        tokio::fs::create_dir_all(self.config.storage_directory.join("docs"))
            .await
            .map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))?;
        let gossip = Gossip::builder().spawn(endpoint.clone());
        let docs = Docs::persistent(self.config.storage_directory.join("docs"))
            .spawn(endpoint.clone(), (*blobs).clone(), gossip.clone())
            .await
            .map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))?;
        let router = Router::builder(endpoint.clone())
            .accept(BLOBS_ALPN, BlobsProtocol::new(&blobs, None))
            .accept(GOSSIP_ALPN, gossip)
            .accept(DOCS_ALPN, docs.clone())
            .spawn();

        let mut node_id = [0u8; ID::MAX_SIZE];
        node_id.copy_from_slice(&blake3::hash(&endpoint_secret).as_bytes()[..ID::MAX_SIZE]);
        if node_id.iter().all(|byte| *byte == 0) {
            node_id[0] = 1;
        }
        let node_id = ID::try_from(&node_id).map_err(|error| {
            NativeError::new(ErrorCode::Storage, format!("invalid HLC node id: {error}"))
        })?;
        let hlc = HLCBuilder::new()
            .with_id(node_id)
            .with_max_delta(Duration::from_millis(self.config.max_hlc_delta_ms))
            .build();
        if let Some(previous) = persistence.last_hlc().await? {
            let timestamp = previous.to_timestamp()?;
            hlc.update_with_timestamp(&timestamp).map_err(|error| {
                NativeError::new(
                    ErrorCode::ClockDrift,
                    format!("persisted HLC is invalid: {error}"),
                )
            })?;
        }

        let watcher_endpoint = endpoint.clone();
        self.persistence = Some(persistence);
        self.endpoint = Some(endpoint);
        self.blobs = Some(blobs);
        self.docs = Some(docs);
        self.router = Some(router);
        self.hlc = hlc;
        self.started = true;
        self.install_path_watcher(watcher_endpoint);

        if let Some(character) = self.character_key.clone()
            && let Err(error) = self.activate_character_author(character).await
        {
            let _ = self.shutdown().await;
            return Err(error);
        }
        if let Err(error) = self.restore_persisted_group().await {
            let _ = self.shutdown().await;
            return Err(error);
        }

        let mut started = NativeEvent::simple(EventKind::Started);
        if let Some(author) = self.author.as_ref() {
            started.actual_author = author.id().as_bytes().to_vec();
        }
        Ok(vec![started])
    }

    async fn restore_persisted_group(&mut self) -> NativeResult<()> {
        let persistence = self
            .persistence
            .as_ref()
            .ok_or_else(|| NativeError::state("native endpoint is not started"))?
            .clone();
        let namespace = persistence.group_namespace().await?;
        let ticket_bytes = persistence.group_ticket().await?;
        match (namespace, ticket_bytes) {
            (None, None) => return Ok(()),
            (Some(_), None) | (None, Some(_)) => {
                return Err(NativeError::new(
                    ErrorCode::Storage,
                    "persisted group namespace and ticket state is incomplete",
                ));
            }
            (Some(namespace), Some(ticket_bytes)) => {
                let ticket_string = std::str::from_utf8(&ticket_bytes).map_err(|_| {
                    NativeError::new(ErrorCode::Storage, "persisted group ticket is not UTF-8")
                })?;
                let ticket: DocTicket = ticket_string.parse().map_err(|_| {
                    NativeError::new(ErrorCode::Storage, "persisted group ticket is invalid")
                })?;
                if *ticket.capability.id().as_bytes() != namespace {
                    return Err(NativeError::new(
                        ErrorCode::Storage,
                        "persisted group ticket namespace does not match manifest",
                    ));
                }
                let docs = self.docs()?.clone();
                let doc = self
                    .open_existing_doc(&docs, ticket.capability.id())
                    .await?
                    .ok_or_else(|| {
                        NativeError::new(
                            ErrorCode::Storage,
                            "persisted group document is unavailable",
                        )
                    })?;
                self.install_subscription(&doc).await?;
                doc.start_sync(ticket.nodes)
                    .await
                    .map_err(|error| NativeError::new(ErrorCode::Network, error.to_string()))?;
                self.doc = Some(doc);
                self.group_open = true;
                self.joined = false;
                self.join_progress = Some(JoinProgress {
                    contacted_peer: None,
                    sync_finished: false,
                    compatible_metadata: false,
                    content_ready: false,
                    content_error: false,
                });
                self.initial_sync_emitted = false;
                self.load_existing_records().await?;
                if let Some(progress) = self.join_progress.as_mut() {
                    progress.content_ready = self.pending_entries.is_empty();
                }
            }
        }
        Ok(())
    }

    fn install_path_watcher(&self, endpoint: Endpoint) {
        let sender = self.live_sender.clone();
        tokio::spawn(async move {
            let panic_sender = sender.clone();
            let result = std::panic::AssertUnwindSafe(async move {
                let mut addresses = endpoint.watch_addr().stream();
                endpoint
                    .closed()
                    .run_until(async move {
                        while N0StreamExt::next(&mut addresses).await.is_some() {
                            if sender.send(LiveInput::PathChanged).await.is_err() {
                                break;
                            }
                        }
                    })
                    .await;
            })
            .catch_unwind()
            .await;
            if result.is_err() {
                let _ = panic_sender
                    .send(LiveInput::Event(Err(
                        "endpoint path watcher panicked".to_owned()
                    )))
                    .await;
            }
        });
    }

    async fn activate_character_author(&mut self, character: Vec<u8>) -> NativeResult<()> {
        let persistence = self
            .persistence
            .as_ref()
            .ok_or_else(|| NativeError::state("native endpoint is not started"))?;
        let bytes = persistence
            .load_or_create_character_author(&character)
            .await?;
        let author = Author::from_bytes(&bytes);
        if let Some(docs) = self.docs.as_ref() {
            docs.author_import(author.clone())
                .await
                .map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))?;
        }
        persistence
            .set_selected_character(Some(character.clone()))
            .await?;
        self.character_key = Some(character);
        self.author = Some(author);
        Ok(())
    }

    pub async fn set_character_author(&mut self, character: Vec<u8>) -> NativeResult<()> {
        if character.is_empty() {
            return Err(NativeError::invalid("character author key cannot be empty"));
        }
        if self.group_open || self.joined {
            return Err(NativeError::state(
                "character author cannot change while a group is active or records are pending",
            ));
        }
        self.character_key = Some(character.clone());
        if self.started {
            self.activate_character_author(character).await?;
        }
        Ok(())
    }

    fn current_author(&self) -> NativeResult<&Author> {
        self.author
            .as_ref()
            .ok_or_else(|| NativeError::state("select a character author before joining"))
    }

    fn docs(&self) -> NativeResult<&Docs> {
        self.docs
            .as_ref()
            .ok_or_else(|| NativeError::state("native endpoint is not started"))
    }

    fn doc(&self) -> NativeResult<&Doc> {
        self.doc
            .as_ref()
            .ok_or_else(|| NativeError::state("no group document is open"))
    }

    fn check_key_and_value(&self, key: &[u8], payload: &[u8]) -> NativeResult<()> {
        if key.is_empty() || key.len() > self.config.max_key_bytes {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "key exceeds configured limit",
            ));
        }
        if payload.len() > self.config.max_value_bytes {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "payload exceeds configured limit",
            ));
        }
        Ok(())
    }

    fn check_entry_size(&self, entry: &Entry) -> NativeResult<()> {
        if entry.key().is_empty() || entry.key().len() > self.config.max_key_bytes {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "remote record key exceeds configured limit",
            ));
        }
        if entry.content_len() > self.config.max_value_bytes as u64 {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "remote record exceeds configured value limit",
            ));
        }
        Ok(())
    }

    fn check_register_capacity(&self, key: &[u8]) -> NativeResult<()> {
        if !self.registers.contains_key(key)
            && self.registers.len() >= self.config.max_snapshot_records
        {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "replicated register count exceeds configured limit",
            ));
        }
        Ok(())
    }

    pub async fn create_group(&mut self) -> NativeResult<Vec<NativeEvent>> {
        if !self.started {
            return Err(NativeError::state("native endpoint is not started"));
        }
        if self.doc.is_some() {
            return Err(NativeError::state("a group document is already open"));
        }
        let author = self.current_author()?.clone();
        let docs = self.docs()?.clone();
        let doc = docs
            .create()
            .await
            .map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))?;
        self.install_subscription(&doc).await?;
        let metadata = GroupMetadata {
            protocol_version: crate::document::PROTOCOL_VERSION,
            backend_kind: "iroh-docs".to_owned(),
            backend_storage_version: 1,
            namespace: *doc.id().as_bytes(),
        };
        let payload = postcard::to_stdvec(&metadata).map_err(NativeError::from)?;
        self.doc = Some(doc.clone());
        self.group_open = true;
        self.joined = true;
        self.join_progress = None;
        self.initial_sync_emitted = false;
        let namespace = *doc.id().as_bytes();
        let persistence = self.persistence.clone();
        let namespace_result = match persistence.as_ref() {
            Some(persistence) => persistence.set_group_namespace(Some(namespace)).await,
            None => Ok(()),
        };
        if let Err(error) = namespace_result {
            let _ = self.leave().await;
            return Err(error);
        }
        let mut events = match self
            .put_local(
                format!("v1/meta/group/{}", hex::encode(author.id().as_bytes())).into_bytes(),
                record_id_from_namespace(&namespace),
                RECORD_TYPE_GROUP_METADATA.to_owned(),
                None,
                1,
                payload,
            )
            .await
        {
            Ok(events) => events,
            Err(error) => {
                let _ = self.leave().await;
                return Err(error);
            }
        };
        let ticket = match doc
            .share(ShareMode::Write, AddrInfoOptions::RelayAndAddresses)
            .await
        {
            Ok(ticket) => ticket,
            Err(error) => {
                let native_error = NativeError::new(ErrorCode::Network, error.to_string());
                let _ = self.leave().await;
                return Err(native_error);
            }
        };
        let mut joined = NativeEvent::simple(EventKind::Joined);
        joined.key = doc.id().as_bytes().to_vec();
        joined.value = ticket.to_string().into_bytes();
        joined.actual_author = author.id().as_bytes().to_vec();
        if let Some(persistence) = self.persistence.as_ref()
            && let Err(error) = persistence
                .set_group_ticket(Some(joined.value.clone()))
                .await
        {
            let _ = self.leave().await;
            return Err(error);
        }
        events.push(joined);
        Ok(events)
    }

    pub async fn join_group(&mut self, ticket_bytes: Vec<u8>) -> NativeResult<Vec<NativeEvent>> {
        if !self.started {
            return Err(NativeError::state("native endpoint is not started"));
        }
        if ticket_bytes.len() > 64 * 1024 {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "group ticket exceeds configured limit",
            ));
        }
        let ticket_string = std::str::from_utf8(&ticket_bytes)
            .map_err(|_| NativeError::invalid("group ticket is not UTF-8"))?;
        let ticket: DocTicket = ticket_string
            .parse()
            .map_err(|_| NativeError::new(ErrorCode::InvalidArgument, "invalid group ticket"))?;
        if let Some(doc) = self.doc.clone() {
            if doc.id() != ticket.capability.id() {
                return Err(NativeError::state(
                    "a different group document is already open",
                ));
            }
            if self.joined {
                return Ok(Vec::new());
            }
            doc.start_sync(ticket.nodes.clone())
                .await
                .map_err(|error| NativeError::new(ErrorCode::Network, error.to_string()))?;
            let mut joining = NativeEvent::simple(EventKind::Joining);
            joining.key = doc.id().as_bytes().to_vec();
            joining.value = ticket.to_string().into_bytes();
            if let Some(author) = self.author.as_ref() {
                joining.actual_author = author.id().as_bytes().to_vec();
            }
            return Ok(vec![joining]);
        }
        let docs = self.docs()?.clone();
        let doc = if let Some(doc) = self
            .open_existing_doc(&docs, ticket.capability.id())
            .await?
        {
            self.install_subscription(&doc).await?;
            doc.start_sync(ticket.nodes.clone())
                .await
                .map_err(|error| NativeError::new(ErrorCode::Network, error.to_string()))?;
            doc
        } else {
            let (doc, mut events) = docs
                .import_and_subscribe(ticket)
                .await
                .map_err(|error| NativeError::new(ErrorCode::Network, error.to_string()))?;
            let sender = self.live_sender.clone();
            tokio::spawn(async move {
                let panic_sender = sender.clone();
                let result = std::panic::AssertUnwindSafe(async move {
                    while let Some(result) = N0StreamExt::next(&mut events).await {
                        let event = result.map_err(|error| error.to_string());
                        if sender.send(LiveInput::Event(event)).await.is_err() {
                            break;
                        }
                    }
                })
                .catch_unwind()
                .await;
                if result.is_err() {
                    let _ = panic_sender
                        .send(LiveInput::Event(Err(
                            "docs subscription task panicked".to_owned()
                        )))
                        .await;
                }
            });
            doc
        };
        self.doc = Some(doc.clone());
        self.group_open = true;
        self.joined = false;
        self.join_progress = Some(JoinProgress {
            contacted_peer: None,
            sync_finished: false,
            compatible_metadata: false,
            content_ready: false,
            content_error: false,
        });
        self.initial_sync_emitted = false;
        let namespace = *self
            .doc
            .as_ref()
            .ok_or_else(|| NativeError::state("joined document is unavailable"))?
            .id()
            .as_bytes();
        let persistence = self.persistence.clone();
        let namespace_result = match persistence.as_ref() {
            Some(persistence) => persistence.set_group_namespace(Some(namespace)).await,
            None => Ok(()),
        };
        if let Err(error) = namespace_result {
            let _ = self.leave().await;
            return Err(error);
        }
        if let Err(error) = self.load_existing_records().await {
            let _ = self.leave().await;
            return Err(error);
        }
        let ticket = match doc
            .share(ShareMode::Write, AddrInfoOptions::RelayAndAddresses)
            .await
        {
            Ok(ticket) => ticket,
            Err(error) => {
                let native_error = NativeError::new(ErrorCode::Network, error.to_string());
                let _ = self.leave().await;
                return Err(native_error);
            }
        };
        let mut joining = NativeEvent::simple(EventKind::Joining);
        joining.key = namespace.to_vec();
        joining.value = ticket.to_string().into_bytes();
        if let Some(author) = self.author.as_ref() {
            joining.actual_author = author.id().as_bytes().to_vec();
        }
        if let Some(persistence) = self.persistence.as_ref()
            && let Err(error) = persistence
                .set_group_ticket(Some(joining.value.clone()))
                .await
        {
            let _ = self.leave().await;
            return Err(error);
        }
        Ok(vec![joining])
    }

    async fn install_subscription(&self, doc: &Doc) -> NativeResult<()> {
        let mut stream = doc
            .subscribe()
            .await
            .map_err(|error| NativeError::new(ErrorCode::Network, error.to_string()))?;
        let sender = self.live_sender.clone();
        tokio::spawn(async move {
            let panic_sender = sender.clone();
            let result = std::panic::AssertUnwindSafe(async move {
                while let Some(result) = N0StreamExt::next(&mut stream).await {
                    let event = result.map_err(|error| error.to_string());
                    if sender.send(LiveInput::Event(event)).await.is_err() {
                        break;
                    }
                }
            })
            .catch_unwind()
            .await;
            if result.is_err() {
                let _ = panic_sender
                    .send(LiveInput::Event(Err(
                        "docs subscription task panicked".to_owned()
                    )))
                    .await;
            }
        });
        Ok(())
    }

    async fn open_existing_doc(
        &self,
        docs: &Docs,
        namespace: iroh_docs::NamespaceId,
    ) -> NativeResult<Option<Doc>> {
        match docs.open(namespace).await {
            Ok(doc) => Ok(doc),
            Err(error) if error.to_string() == "Replica not found" => Ok(None),
            Err(error) => Err(NativeError::new(ErrorCode::Storage, error.to_string())),
        }
    }

    async fn load_existing_records(&mut self) -> NativeResult<()> {
        let doc = self.doc()?.clone();
        let stream = doc
            .get_many(Query::all())
            .await
            .map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))?;
        futures_util::pin_mut!(stream);
        while let Some(result) = N0StreamExt::next(&mut stream).await {
            let entry =
                result.map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))?;
            self.check_entry_size(&entry)?;
            let hash = *entry.content_hash().as_bytes();
            match self.read_blob(entry.content_hash()).await {
                Ok(bytes) => {
                    let _ = self.accept_entry(entry, bytes).await?;
                    self.pending_entries.remove(&hash);
                }
                Err(_) => {
                    if !self.pending_entries.contains_key(&hash)
                        && self.pending_entries.len() >= self.config.max_snapshot_records
                    {
                        return Err(NativeError::new(
                            ErrorCode::LimitExceeded,
                            "pending remote content exceeds configured register limit",
                        ));
                    }
                    let pending = self.pending_entries.entry(hash).or_default();
                    if pending.len() >= 64 {
                        return Err(NativeError::new(
                            ErrorCode::LimitExceeded,
                            "duplicate pending remote content exceeds native limit",
                        ));
                    }
                    pending.push(entry);
                }
            }
        }
        Ok(())
    }

    async fn read_blob(&self, hash: iroh_blobs::Hash) -> NativeResult<Vec<u8>> {
        let blobs = self
            .blobs
            .as_ref()
            .ok_or_else(|| NativeError::state("blob store is not open"))?;
        blobs
            .blobs()
            .get_bytes(hash)
            .await
            .map(|bytes| bytes.to_vec())
            .map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))
    }

    async fn accept_entry(
        &mut self,
        entry: Entry,
        bytes: Vec<u8>,
    ) -> NativeResult<Option<MeshEnvelope>> {
        if bytes.len() > self.config.max_value_bytes {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "remote record exceeds value limit",
            ));
        }
        let expected_hash = *entry.content_hash().as_bytes();
        if *blake3::hash(&bytes).as_bytes() != expected_hash {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "docs content hash mismatch",
            ));
        }
        let envelope = MeshEnvelope::decode(&bytes, self.config.max_value_bytes)?;
        let docs_author = entry.author();
        envelope.verify(docs_author, entry.key())?;
        validate_record_key(entry.key(), &envelope.record_type, docs_author)?;
        envelope.validate_record_id_for_key()?;

        if let Some(current) = self.registers.get(entry.key()) {
            if current.actual_author != envelope.actual_author {
                return Err(NativeError::new(
                    ErrorCode::InvalidRecord,
                    "register author changed",
                ));
            }
            if envelope.revision > current.revision && envelope.hlc < current.hlc {
                return Err(NativeError::new(
                    ErrorCode::InvalidRecord,
                    "same-author revision carries a regressing HLC",
                ));
            }
            match envelope.revision.cmp(&current.revision) {
                std::cmp::Ordering::Less => return Ok(None),
                std::cmp::Ordering::Equal => {
                    let current_hash = current.content_hash()?;
                    if current_hash == expected_hash {
                        return Ok(None);
                    }
                    let variants = self.forks.entry(entry.key().to_vec()).or_default();
                    if variants.is_empty() {
                        variants.insert(current_hash.to_vec(), current.clone());
                    }
                    if variants.len() >= 64 {
                        return Err(NativeError::new(
                            ErrorCode::LimitExceeded,
                            "same-author fork variants exceed native limit",
                        ));
                    }
                    variants.insert(expected_hash.to_vec(), envelope.clone());
                    return Err(NativeError::new(
                        ErrorCode::InvalidRecord,
                        "same-author equal-revision fork quarantined",
                    ));
                }
                std::cmp::Ordering::Greater => {}
            }
        }
        let authoritative_repair = self
            .registers
            .get(entry.key())
            .is_some_and(|current| envelope.revision > current.revision);
        if self.forks.contains_key(entry.key()) && !authoritative_repair {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "forked register is excluded until authoritative repair",
            ));
        }
        if envelope.record_type == RECORD_TYPE_GROUP_METADATA {
            let metadata: GroupMetadata =
                postcard::from_bytes(&envelope.payload).map_err(NativeError::from)?;
            let namespace_matches = self
                .doc
                .as_ref()
                .is_some_and(|doc| metadata.namespace == *doc.id().as_bytes());
            if metadata.protocol_version != crate::document::PROTOCOL_VERSION
                || metadata.backend_kind != "iroh-docs"
                || metadata.backend_storage_version != 1
                || !namespace_matches
            {
                return Err(NativeError::new(
                    ErrorCode::InvalidRecord,
                    "incompatible group metadata",
                ));
            }
            if let Some(progress) = self.join_progress.as_mut() {
                progress.compatible_metadata = true;
            }
        }
        self.check_register_capacity(entry.key())?;

        // Do not mutate the live clock until every revision, fork, key, metadata, and capacity
        // check above has admitted this record as the actionable register value.
        let timestamp = envelope.to_timestamp()?;
        validate_incoming_hlc(&self.config, &timestamp)?;
        self.hlc
            .update_with_timestamp(&timestamp)
            .map_err(|error| {
                NativeError::new(
                    ErrorCode::ClockDrift,
                    format!("remote HLC quarantined: {error}"),
                )
            })?;
        let persisted_hlc = self.hlc.new_timestamp();
        if authoritative_repair
            && self.forks.remove(entry.key()).is_some()
            && self.forks.is_empty()
            && self.pending_entries.is_empty()
            && let Some(progress) = self.join_progress.as_mut()
        {
            progress.content_error = false;
        }
        self.registers
            .insert(entry.key().to_vec(), envelope.clone());
        if let Some(persistence) = self.persistence.as_ref() {
            persistence
                .save_hlc(HlcWire::from_timestamp(&persisted_hlc))
                .await?;
            persistence
                .remember_revision(entry.key(), envelope.revision)
                .await?;
        }
        Ok(Some(envelope))
    }

    async fn put_local(
        &mut self,
        key: Vec<u8>,
        record_id: [u8; RECORD_ID_BYTES],
        record_type: String,
        generation: Option<u64>,
        revision: u64,
        payload: Vec<u8>,
    ) -> NativeResult<Vec<NativeEvent>> {
        self.check_key_and_value(&key, &payload)?;
        if revision == 0 {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "author revisions start at one",
            ));
        }
        let author = self.current_author()?.clone();
        let doc = self.doc()?.clone();
        validate_record_key(&key, &record_type, author.id())?;
        validate_record_id_bytes(&record_id)?;
        validate_record_id_for_key(&key, &record_type, &record_id)?;
        self.check_register_capacity(&key)?;
        if self
            .registers
            .get(&key)
            .is_some_and(|current| current.revision >= revision)
        {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "local revision is not greater than the current register",
            ));
        }
        if self.forks.contains_key(&key)
            && self
                .registers
                .get(&key)
                .is_none_or(|current| revision <= current.revision)
        {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "forked register requires a strictly newer repair revision",
            ));
        }
        if let Some(persistence) = self.persistence.as_ref() {
            let reserved = persistence
                .reserve_revision(&key, revision.saturating_sub(1))
                .await?;
            if reserved != revision {
                return Err(NativeError::new(
                    ErrorCode::InvalidRecord,
                    "requested revision does not match the durable reservation",
                ));
            }
        }
        let envelope = MeshEnvelope::new(
            &author,
            MeshEnvelopeInput {
                key: key.clone(),
                record_id,
                record_type,
                generation,
                revision,
                payload,
            },
            &self.hlc,
        )?;
        let encoded = envelope.encode()?;
        if encoded.len() > self.config.max_value_bytes {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "encoded envelope exceeds configured value limit",
            ));
        }
        doc.set_bytes(author.id(), Bytes::from(key), Bytes::from(encoded.clone()))
            .await
            .map_err(|error| NativeError::new(ErrorCode::Storage, error.to_string()))?;
        self.registers
            .insert(envelope.key.clone(), envelope.clone());
        if self.forks.remove(&envelope.key).is_some()
            && let Some(progress) = self.join_progress.as_mut()
        {
            progress.content_error = false;
        }
        if let Some(persistence) = self.persistence.as_ref() {
            persistence.save_hlc(envelope.hlc).await?;
            persistence
                .remember_revision(&envelope.key, revision)
                .await?;
        }
        Ok(vec![NativeEvent::record(
            &envelope,
            EventKind::RecordInserted,
        )])
    }

    pub async fn put(
        &mut self,
        key: Vec<u8>,
        record_id: [u8; RECORD_ID_BYTES],
        record_type: String,
        generation: Option<u64>,
        revision: u64,
        payload: Vec<u8>,
    ) -> NativeResult<Vec<NativeEvent>> {
        if !self.started || !self.joined {
            return Err(NativeError::state("join a group before putting records"));
        }
        self.put_local(key, record_id, record_type, generation, revision, payload)
            .await
    }

    pub async fn handle_live(&mut self, input: LiveInput) -> NativeResult<Vec<NativeEvent>> {
        let LiveInput::Event(result) = input else {
            let mut event = NativeEvent::simple(EventKind::PathChanged);
            event.key = self.endpoint_id();
            return Ok(vec![event]);
        };
        let live = result.map_err(|message| NativeError::new(ErrorCode::Network, message))?;
        // A joining replica must process the initial reconciliation stream before it can be
        // marked joined.  `group_open` is the local admission boundary; `joined` is the derived
        // readiness result and therefore cannot gate the events that establish readiness.
        if !self.group_open {
            return Ok(Vec::new());
        }
        match live {
            LiveEvent::InsertLocal { entry } | LiveEvent::InsertRemote { entry, .. } => {
                self.check_entry_size(&entry)?;
                let hash = *entry.content_hash().as_bytes();
                match self.read_blob(entry.content_hash()).await {
                    Ok(bytes) => {
                        let accepted = match self.accept_entry(entry, bytes).await {
                            Ok(accepted) => accepted,
                            Err(error) => {
                                self.mark_content_error();
                                return Err(error);
                            }
                        };
                        match accepted {
                            Some(envelope) => {
                                let mut events =
                                    vec![NativeEvent::record(&envelope, EventKind::RecordInserted)];
                                if self.initial_sync_complete() {
                                    events.extend(self.initial_sync_events());
                                }
                                Ok(events)
                            }
                            None => Ok(Vec::new()),
                        }
                    }
                    Err(error) => {
                        if !self.pending_entries.contains_key(&hash)
                            && self.pending_entries.len() >= self.config.max_snapshot_records
                        {
                            return Err(NativeError::new(
                                ErrorCode::LimitExceeded,
                                "pending remote content exceeds configured register limit",
                            ));
                        }
                        let pending = self.pending_entries.entry(hash).or_default();
                        if pending.len() >= 64 {
                            return Err(NativeError::new(
                                ErrorCode::LimitExceeded,
                                "duplicate pending remote content exceeds native limit",
                            ));
                        }
                        pending.push(entry);
                        Ok(vec![warning_event(error.message)])
                    }
                }
            }
            LiveEvent::ContentReady { hash } => {
                let hash_bytes = *hash.as_bytes();
                let Some(entries) = self.pending_entries.remove(&hash_bytes) else {
                    return Ok(Vec::new());
                };
                if let Some(error) = entries
                    .iter()
                    .find_map(|entry| self.check_entry_size(entry).err())
                {
                    self.pending_entries.insert(hash_bytes, entries);
                    return Err(error);
                }
                let bytes = match self.read_blob(hash).await {
                    Ok(bytes) => bytes,
                    Err(error) => {
                        self.pending_entries.insert(hash_bytes, entries);
                        return Err(error);
                    }
                };
                let mut events = Vec::new();
                let mut remaining = entries.into_iter();
                while let Some(entry) = remaining.next() {
                    match self.accept_entry(entry, bytes.clone()).await {
                        Ok(Some(envelope)) => {
                            events.push(NativeEvent::record(&envelope, EventKind::RecordInserted));
                        }
                        Ok(None) => {}
                        Err(error) => {
                            let rest = remaining.collect::<Vec<_>>();
                            if !rest.is_empty() {
                                self.pending_entries
                                    .entry(hash_bytes)
                                    .or_default()
                                    .extend(rest);
                            }
                            self.mark_content_error();
                            return Err(error);
                        }
                    }
                }
                if self.initial_sync_complete() {
                    events.extend(self.initial_sync_events());
                }
                Ok(events)
            }
            LiveEvent::NeighborUp(peer) => {
                if let Some(progress) = self.join_progress.as_mut() {
                    progress.contacted_peer = Some(*peer.as_bytes());
                }
                let mut event = NativeEvent::simple(EventKind::PeerConnected);
                event.actual_author = peer.as_bytes().to_vec();
                let mut events = vec![event];
                if self.initial_sync_complete() {
                    events.extend(self.initial_sync_events());
                }
                Ok(events)
            }
            LiveEvent::NeighborDown(peer) => {
                let mut event = NativeEvent::simple(EventKind::PeerDisconnected);
                event.actual_author = peer.as_bytes().to_vec();
                Ok(vec![event])
            }
            LiveEvent::SyncFinished(_) => {
                if let Some(progress) = self.join_progress.as_mut() {
                    progress.sync_finished = true;
                }
                if self.initial_sync_complete() {
                    Ok(self.initial_sync_events())
                } else {
                    Ok(Vec::new())
                }
            }
            LiveEvent::PendingContentReady => {
                if let Some(progress) = self.join_progress.as_mut() {
                    progress.content_ready = true;
                }
                if self.initial_sync_complete() {
                    Ok(self.initial_sync_events())
                } else {
                    Ok(Vec::new())
                }
            }
        }
    }

    fn initial_sync_complete(&self) -> bool {
        !self.initial_sync_emitted
            && self.join_progress.as_ref().is_some_and(|progress| {
                progress.contacted_peer.is_some()
                    && progress.sync_finished
                    && progress.compatible_metadata
                    && progress.content_ready
                    && !progress.content_error
                    && self.forks.is_empty()
                    && self.pending_entries.is_empty()
                    && self.snapshot_records().is_ok()
            })
    }

    fn mark_content_error(&mut self) {
        if let Some(progress) = self.join_progress.as_mut() {
            progress.content_error = true;
        }
    }

    fn initial_sync_events(&mut self) -> Vec<NativeEvent> {
        self.initial_sync_emitted = true;
        self.joined = true;
        let mut joined = NativeEvent::simple(EventKind::Joined);
        joined.key = self.namespace_id();
        let mut events = vec![joined];
        let mut event = NativeEvent::simple(EventKind::InitialSyncCompleted);
        if let Some(progress) = &self.join_progress
            && let Some(peer) = progress.contacted_peer
        {
            event.actual_author = peer.to_vec();
        }
        event.aux = self.registers.len() as u64;
        events.push(event);
        events
    }

    pub async fn leave(&mut self) -> NativeResult<Vec<NativeEvent>> {
        self.leave_inner(true).await
    }

    async fn leave_inner(&mut self, clear_persisted_group: bool) -> NativeResult<Vec<NativeEvent>> {
        if !self.group_open {
            return Ok(Vec::new());
        }
        let mut failure = None;
        if let Some(doc) = self.doc.take() {
            if clear_persisted_group && let Err(error) = doc.leave().await {
                failure = Some(NativeError::new(ErrorCode::Network, error.to_string()));
            }
            if let Err(error) = doc.close().await
                && failure.is_none()
            {
                failure = Some(NativeError::new(ErrorCode::Storage, error.to_string()));
            }
        }
        self.group_open = false;
        self.joined = false;
        self.join_progress = None;
        self.initial_sync_emitted = false;
        self.registers.clear();
        self.forks.clear();
        self.pending_entries.clear();
        if clear_persisted_group && let Some(persistence) = self.persistence.as_ref() {
            persistence.set_group_namespace(None).await?;
            persistence.set_group_ticket(None).await?;
        }
        if let Some(error) = failure {
            return Err(error);
        }
        Ok(vec![NativeEvent::simple(EventKind::Left)])
    }

    pub async fn shutdown(&mut self) -> NativeResult<Vec<NativeEvent>> {
        let mut events = match self.leave_inner(false).await {
            Ok(events) => events,
            Err(error) => {
                self.joined = false;
                self.join_progress = None;
                self.initial_sync_emitted = false;
                self.registers.clear();
                self.forks.clear();
                self.pending_entries.clear();
                vec![warning_event(format!(
                    "group leave during shutdown failed: {}",
                    error.message
                ))]
            }
        };
        if let Some(router) = self.router.take()
            && let Err(error) = router.shutdown().await
        {
            events.push(warning_event(format!("router shutdown failed: {error}")));
        }
        if let Some(blobs) = self.blobs.take()
            && let Err(error) = blobs.shutdown().await
        {
            events.push(warning_event(format!(
                "blob store shutdown failed: {error}"
            )));
        }
        self.docs = None;
        self.endpoint = None;
        self.started = false;
        events.push(NativeEvent::simple(EventKind::Stopped));
        Ok(events)
    }

    pub fn snapshot_records(&self) -> NativeResult<Vec<SnapshotRecord>> {
        if self.registers.len() > self.config.max_snapshot_records {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "snapshot record count exceeds configured limit",
            ));
        }
        self.registers
            .iter()
            .filter(|(key, _)| !self.forks.contains_key(*key))
            .map(|(key, envelope)| {
                let value = envelope.encode()?;
                let content_hash = envelope.content_hash()?;
                Ok(SnapshotRecord {
                    protocol_version: envelope.protocol_version,
                    key: key.clone(),
                    value,
                    actual_author: envelope.actual_author.to_vec(),
                    content_hash: content_hash.to_vec(),
                    generation: envelope.generation,
                    revision: envelope.revision,
                    record_type: envelope.record_type.clone(),
                    hlc: envelope.hlc,
                })
            })
            .collect()
    }
}

fn warning_event(message: String) -> NativeEvent {
    let mut event = NativeEvent::simple(EventKind::Warning);
    event.value = message.into_bytes();
    event
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::{Digest, Sha256};
    use uhlc::{ID, NTP64};

    #[test]
    fn supported_key_shapes_require_authenticated_owner_and_matching_type() {
        let author = Author::from_bytes(&[0x11; 32]);
        let owner = hex::encode(author.id().as_bytes());
        let list_id = "11111111-1111-1111-1111-111111111111";

        assert!(
            validate_record_key(
                format!("v1/meta/group/{owner}").as_bytes(),
                RECORD_TYPE_GROUP_METADATA,
                author.id(),
            )
            .is_ok()
        );
        assert!(
            validate_record_key(
                format!("v1/lists/{owner}/{list_id}").as_bytes(),
                RECORD_TYPE_PUBLISHED_LIST,
                author.id(),
            )
            .is_ok()
        );
        assert!(
            validate_record_key(
                format!("v1/capability-requests/{owner}/{list_id}").as_bytes(),
                RECORD_TYPE_CAPABILITY_REQUEST,
                author.id(),
            )
            .is_ok()
        );
        assert!(
            validate_record_key(
                format!("v1/workers/{owner}").as_bytes(),
                RECORD_TYPE_WORKER_SESSION,
                author.id(),
            )
            .is_ok()
        );
        assert!(
            validate_record_key(
                format!("v1/chest/{owner}").as_bytes(),
                RECORD_TYPE_CHEST_SNAPSHOT,
                author.id(),
            )
            .is_ok()
        );
        assert!(
            validate_record_key(
                format!("v1/capability-responses/{owner}/{list_id}").as_bytes(),
                RECORD_TYPE_CAPABILITY_RESPONSE,
                author.id(),
            )
            .is_ok()
        );
        assert!(
            validate_record_key(
                format!("v1/transfers/{owner}/{list_id}").as_bytes(),
                RECORD_TYPE_INVENTORY_TRANSFER,
                author.id(),
            )
            .is_ok()
        );
        let list_key = format!("v1/lists/{owner}/{list_id}");
        let matching_id = parse_record_id(list_id).expect("test list id should parse");
        assert!(
            validate_record_id_for_key(
                list_key.as_bytes(),
                RECORD_TYPE_PUBLISHED_LIST,
                &matching_id,
            )
            .is_ok()
        );
        let wrong_id = parse_record_id("22222222-2222-2222-2222-222222222222")
            .expect("test wrong id should parse");
        assert!(
            validate_record_id_for_key(list_key.as_bytes(), RECORD_TYPE_PUBLISHED_LIST, &wrong_id,)
                .is_err()
        );

        assert!(
            validate_record_key(
                format!("v1/meta/group/ns/{owner}").as_bytes(),
                RECORD_TYPE_GROUP_METADATA,
                author.id(),
            )
            .is_err()
        );
        assert!(
            validate_record_key(
                format!("v1/lists/{}/{list_id}", hex::encode([0x22; 32])).as_bytes(),
                RECORD_TYPE_PUBLISHED_LIST,
                author.id(),
            )
            .is_err()
        );
        assert!(
            validate_record_key(
                format!("v1/lists/{owner}/not-a-guid").as_bytes(),
                RECORD_TYPE_PUBLISHED_LIST,
                author.id(),
            )
            .is_err()
        );
        assert!(
            validate_record_key(
                format!("v1/lists/{owner}/{list_id}").as_bytes(),
                RECORD_TYPE_WORKER_SESSION,
                author.id(),
            )
            .is_err()
        );
        assert!(
            validate_record_key(
                format!("v1/unknown/{owner}").as_bytes(),
                RECORD_TYPE_WORKER_SESSION,
                author.id(),
            )
            .is_err()
        );
        assert!(
            validate_record_key(
                format!("v1/transfers/{owner}/{list_id}/extra").as_bytes(),
                RECORD_TYPE_INVENTORY_TRANSFER,
                author.id(),
            )
            .is_err()
        );
    }

    #[test]
    fn incoming_hlc_policy_rejects_excessive_drift_before_live_clock_update() {
        let config = NativeConfig::default();
        let node_id = ID::try_from(&[0x33; ID::MAX_SIZE]).expect("test HLC node id is valid");
        let future = Timestamp::new(NTP64(u64::MAX), node_id);
        let result = validate_incoming_hlc(&config, &future);
        assert!(matches!(
            result,
            Err(NativeError {
                code: ErrorCode::ClockDrift,
                ..
            })
        ));
    }

    #[test]
    fn initial_sync_promotes_joined_only_after_all_readiness_inputs() {
        let (sender, _receiver) = mpsc::channel(1);
        let mut node = NativeNode::new(NativeConfig::default(), sender);
        node.group_open = true;
        node.join_progress = Some(JoinProgress {
            contacted_peer: None,
            sync_finished: true,
            compatible_metadata: true,
            content_ready: true,
            content_error: false,
        });
        assert!(!node.initial_sync_complete());
        node.join_progress
            .as_mut()
            .expect("join progress should exist")
            .contacted_peer = Some([0x44; 32]);
        assert!(node.initial_sync_complete());
        let events = node.initial_sync_events();
        assert_eq!(events[0].kind, EventKind::Joined);
        assert_eq!(events[1].kind, EventKind::InitialSyncCompleted);
        assert!(node.is_joined());
    }

    #[test]
    fn snapshot_hash_is_full_envelope_blake3_not_payload_sha256() {
        let author = Author::from_bytes(&[0x55; 32]);
        let hlc = HLCBuilder::new().build();
        let key = format!("v1/workers/{}", hex::encode(author.id().as_bytes())).into_bytes();
        let envelope = MeshEnvelope::new(
            &author,
            MeshEnvelopeInput {
                key: key.clone(),
                record_id: [0x66; RECORD_ID_BYTES],
                record_type: RECORD_TYPE_WORKER_SESSION.to_owned(),
                generation: None,
                revision: 1,
                payload: b"snapshot-payload".to_vec(),
            },
            &hlc,
        )
        .expect("snapshot envelope should sign");
        let encoded = envelope.encode().expect("snapshot envelope should encode");
        let expected_content_hash = *blake3::hash(&encoded).as_bytes();
        let payload_hash = Sha256::digest(b"snapshot-payload");

        let (sender, _receiver) = mpsc::channel(1);
        let mut node = NativeNode::new(NativeConfig::default(), sender);
        node.registers.insert(key, envelope);
        let records = node
            .snapshot_records()
            .expect("snapshot records should be prepared");
        assert_eq!(records.len(), 1);
        assert_eq!(
            records[0].protocol_version,
            crate::document::PROTOCOL_VERSION
        );
        assert_eq!(records[0].value, encoded);
        assert_eq!(records[0].content_hash, expected_content_hash);
        assert_ne!(records[0].content_hash, payload_hash.as_slice());
    }
}
