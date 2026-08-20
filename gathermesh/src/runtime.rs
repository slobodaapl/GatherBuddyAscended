use std::{
    collections::{BTreeMap, HashMap},
    panic::AssertUnwindSafe,
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicU32, AtomicU64, Ordering},
    },
    time::{Duration, Instant},
};

use futures_util::FutureExt;
use tokio::{
    runtime::{Builder, Runtime},
    sync::mpsc,
    time,
};

use crate::{
    document::RECORD_ID_BYTES,
    error::{ErrorCode, NativeError, NativeResult},
    events::{EventKind, EventQueue, NativeEvent, PreparedSnapshot, SnapshotRecord},
    node::{LiveInput, NativeConfig, NativeNode},
};

pub const LIFE_CREATED: u32 = 1;
pub const LIFE_STARTING: u32 = 2;
pub const LIFE_RUNNING: u32 = 3;
pub const LIFE_CLOSING: u32 = 4;
pub const LIFE_CLOSED: u32 = 5;
pub const LIFE_DESTROYED: u32 = 6;
pub const DEFAULT_SHUTDOWN_TIMEOUT_MS: u32 = 5_000;
const MAX_SHUTDOWN_TIMEOUT_MS: u32 = 300_000;

static BACKGROUND_RUNTIME: OnceLock<Runtime> = OnceLock::new();
static NEXT_SNAPSHOT_HANDLE: AtomicU64 = AtomicU64::new(1);
static SNAPSHOT_HANDLES: OnceLock<Mutex<HashMap<u64, Arc<SnapshotCursor>>>> = OnceLock::new();
static GLOBAL_ERRORS: OnceLock<Arc<ErrorRegistry>> = OnceLock::new();
const MAX_OPEN_SNAPSHOT_HANDLES: usize = 8;

fn runtime() -> &'static Runtime {
    BACKGROUND_RUNTIME.get_or_init(|| {
        Builder::new_multi_thread()
            .enable_all()
            .thread_name("gathermesh")
            .build()
            .expect("gathermesh background runtime must be constructible")
    })
}

fn snapshot_handles() -> &'static Mutex<HashMap<u64, Arc<SnapshotCursor>>> {
    SNAPSHOT_HANDLES.get_or_init(|| Mutex::new(HashMap::new()))
}

pub fn global_errors() -> Arc<ErrorRegistry> {
    GLOBAL_ERRORS
        .get_or_init(|| Arc::new(ErrorRegistry::new(512)))
        .clone()
}

#[derive(Debug)]
pub enum Command {
    Start,
    CreateGroup,
    JoinGroup(Vec<u8>),
    Leave,
    Put(PutCommand),
    SetCharacterAuthor(Vec<u8>),
    RequestSnapshot(u64),
    Shutdown { timeout_ms: u32 },
}

#[derive(Debug)]
pub struct PutCommand {
    pub key: Vec<u8>,
    pub record_id: [u8; RECORD_ID_BYTES],
    pub record_type: String,
    pub generation: Option<u64>,
    pub revision: u64,
    pub payload: Vec<u8>,
}

#[derive(Debug)]
pub struct ErrorRegistry {
    next: AtomicU64,
    values: Mutex<BTreeMap<u64, String>>,
    capacity: usize,
}

impl ErrorRegistry {
    pub fn new(capacity: usize) -> Self {
        Self {
            next: AtomicU64::new(1),
            values: Mutex::new(BTreeMap::new()),
            capacity: capacity.max(1),
        }
    }

    pub fn insert(&self, message: impl Into<String>) -> u64 {
        let id = self.next.fetch_add(1, Ordering::Relaxed);
        let mut values = match self.values.lock() {
            Ok(values) => values,
            Err(poisoned) => poisoned.into_inner(),
        };
        if values.len() >= self.capacity
            && let Some(oldest) = values.keys().next().copied()
        {
            values.remove(&oldest);
        }
        let mut message = message.into();
        if message.len() > 4096 {
            let mut limit = 4096;
            while !message.is_char_boundary(limit) {
                limit -= 1;
            }
            message.truncate(limit);
        }
        values.insert(id, message);
        id
    }

    pub fn get(&self, id: u64) -> Option<Vec<u8>> {
        self.values
            .lock()
            .ok()?
            .get(&id)
            .map(|message| message.as_bytes().to_vec())
    }

    pub fn remove(&self, id: u64) {
        if let Ok(mut values) = self.values.lock() {
            values.remove(&id);
        }
    }
}

#[derive(Debug)]
pub struct StatusState {
    pub lifecycle: AtomicU32,
    pub joined: AtomicU32,
    pub world_epoch: AtomicU64,
    pub pending_commands: AtomicU64,
    pub pending_events: AtomicU64,
    pub endpoint_id: Mutex<Vec<u8>>,
    pub namespace_id: Mutex<Vec<u8>>,
    pub last_error: AtomicU64,
}

impl StatusState {
    fn new() -> Self {
        Self {
            lifecycle: AtomicU32::new(LIFE_CREATED),
            joined: AtomicU32::new(0),
            world_epoch: AtomicU64::new(0),
            pending_commands: AtomicU64::new(0),
            pending_events: AtomicU64::new(0),
            endpoint_id: Mutex::new(Vec::new()),
            namespace_id: Mutex::new(Vec::new()),
            last_error: AtomicU64::new(0),
        }
    }
}

#[derive(Debug, Clone)]
pub struct StatusSnapshot {
    pub lifecycle: u32,
    pub joined: bool,
    pub world_epoch: u64,
    pub pending_commands: u64,
    pub pending_events: u64,
    pub endpoint_id: Vec<u8>,
    pub namespace_id: Vec<u8>,
    pub last_error: u64,
}

#[derive(Debug)]
struct SnapshotCursor {
    prepared: Arc<PreparedSnapshot>,
    cursor: Mutex<usize>,
    lifetime: Duration,
    events: Arc<Mutex<EventQueue>>,
}

#[derive(Debug)]
pub struct Service {
    command_sender: mpsc::Sender<Command>,
    command_gate: Mutex<()>,
    events: Arc<Mutex<EventQueue>>,
    errors: Arc<ErrorRegistry>,
    status: Arc<StatusState>,
    snapshots: Arc<Mutex<BTreeMap<u64, Arc<PreparedSnapshot>>>>,
    config: NativeConfig,
}

impl Service {
    pub fn new(config: NativeConfig) -> Arc<Self> {
        let (command_sender, command_receiver) = mpsc::channel(config.command_capacity.max(1));
        let (live_sender, live_receiver) = mpsc::channel(config.command_capacity.max(1));
        let service = Arc::new(Self {
            command_sender,
            command_gate: Mutex::new(()),
            events: Arc::new(Mutex::new(EventQueue::new(config.event_capacity))),
            errors: global_errors(),
            status: Arc::new(StatusState::new()),
            snapshots: Arc::new(Mutex::new(BTreeMap::new())),
            config: config.clone(),
        });
        let task_service = service.clone();
        runtime().spawn(async move {
            let result = AssertUnwindSafe(run_service(
                task_service.clone(),
                command_receiver,
                live_sender,
                live_receiver,
            ))
            .catch_unwind()
            .await;
            if result.is_err() {
                let error_id = task_service
                    .errors
                    .insert("background native supervisor panicked");
                task_service
                    .status
                    .last_error
                    .store(error_id, Ordering::Release);
                if task_service.status.lifecycle.load(Ordering::Acquire) != LIFE_DESTROYED {
                    task_service
                        .status
                        .lifecycle
                        .store(LIFE_CLOSED, Ordering::Release);
                }
                emit(
                    &task_service,
                    NativeEvent {
                        protocol_version: crate::document::PROTOCOL_VERSION,
                        kind: EventKind::Error,
                        sequence: 0,
                        world_epoch: 0,
                        key: Vec::new(),
                        value: b"background native supervisor panicked".to_vec(),
                        actual_author: Vec::new(),
                        content_hash: Vec::new(),
                        aux: error_id,
                    },
                );
            }
        });
        service
    }

    pub fn mark_destroyed(&self) {
        self.status
            .lifecycle
            .store(LIFE_DESTROYED, Ordering::Release);
        let sender = self.command_sender.clone();
        let status = self.status.clone();
        status.pending_commands.fetch_add(1, Ordering::Relaxed);
        if sender
            .try_send(Command::Shutdown {
                timeout_ms: DEFAULT_SHUTDOWN_TIMEOUT_MS,
            })
            .is_err()
        {
            runtime().spawn(async move {
                if sender
                    .send(Command::Shutdown {
                        timeout_ms: DEFAULT_SHUTDOWN_TIMEOUT_MS,
                    })
                    .await
                    .is_err()
                {
                    status.pending_commands.fetch_sub(1, Ordering::Relaxed);
                }
            });
        }
    }

    pub fn try_send(&self, command: Command) -> NativeResult<()> {
        let _gate = self.command_gate.lock().map_err(|_| {
            NativeError::new(ErrorCode::Internal, "command admission lock poisoned")
        })?;
        match self.status.lifecycle.load(Ordering::Acquire) {
            LIFE_CLOSING => {
                return Err(NativeError::new(
                    ErrorCode::Closing,
                    "native service is closing",
                ));
            }
            LIFE_CLOSED | LIFE_DESTROYED => {
                return Err(NativeError::state("native service is closed"));
            }
            _ => {}
        }
        self.enqueue_command(command)
    }

    fn enqueue_command(&self, command: Command) -> NativeResult<()> {
        self.status.pending_commands.fetch_add(1, Ordering::Relaxed);
        if let Err(error) = self.command_sender.try_send(command) {
            self.status.pending_commands.fetch_sub(1, Ordering::Relaxed);
            return Err(match error {
                mpsc::error::TrySendError::Full(_) => {
                    NativeError::new(ErrorCode::QueueFull, "native command queue is full")
                }
                mpsc::error::TrySendError::Closed(_) => {
                    NativeError::new(ErrorCode::Closing, "native command queue is closed")
                }
            });
        }
        Ok(())
    }

    pub fn start(&self) -> NativeResult<()> {
        let _gate = self.command_gate.lock().map_err(|_| {
            NativeError::new(ErrorCode::Internal, "command admission lock poisoned")
        })?;
        self.status
            .lifecycle
            .compare_exchange(
                LIFE_CREATED,
                LIFE_STARTING,
                Ordering::AcqRel,
                Ordering::Acquire,
            )
            .map_err(|state| {
                NativeError::state(format!("cannot start service from lifecycle {state}"))
            })?;
        if let Err(error) = self.enqueue_command(Command::Start) {
            self.status.lifecycle.store(LIFE_CREATED, Ordering::Release);
            return Err(error);
        }
        Ok(())
    }

    pub fn shutdown(&self, timeout_ms: u32) -> NativeResult<()> {
        if timeout_ms > MAX_SHUTDOWN_TIMEOUT_MS {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "shutdown timeout exceeds native limit",
            ));
        }
        let _gate = self.command_gate.lock().map_err(|_| {
            NativeError::new(ErrorCode::Internal, "command admission lock poisoned")
        })?;
        let current = self.status.lifecycle.load(Ordering::Acquire);
        if current == LIFE_CLOSED || current == LIFE_DESTROYED {
            return Ok(());
        }
        if current == LIFE_CLOSING {
            return Ok(());
        }
        if let Err(state) = self.status.lifecycle.compare_exchange(
            current,
            LIFE_CLOSING,
            Ordering::AcqRel,
            Ordering::Acquire,
        ) {
            if state == LIFE_CLOSED || state == LIFE_DESTROYED {
                return Ok(());
            }
            return Err(NativeError::state(format!(
                "cannot shut down service from lifecycle {state}"
            )));
        }
        if let Err(error) = self.enqueue_command(Command::Shutdown {
            timeout_ms: timeout_ms.max(1),
        }) {
            let _ = self.status.lifecycle.compare_exchange(
                LIFE_CLOSING,
                current,
                Ordering::AcqRel,
                Ordering::Acquire,
            );
            return Err(error);
        }
        Ok(())
    }

    pub fn poll_event(&self) -> Option<NativeEvent> {
        let mut events = self.events.lock().ok()?;
        let event = events.pop();
        self.status
            .pending_events
            .store(events.len() as u64, Ordering::Relaxed);
        event
    }

    pub fn status(&self) -> StatusSnapshot {
        StatusSnapshot {
            lifecycle: self.status.lifecycle.load(Ordering::Acquire),
            joined: self.status.joined.load(Ordering::Acquire) != 0,
            world_epoch: self.status.world_epoch.load(Ordering::Acquire),
            pending_commands: self.status.pending_commands.load(Ordering::Acquire),
            pending_events: self.status.pending_events.load(Ordering::Acquire),
            endpoint_id: self
                .status
                .endpoint_id
                .lock()
                .map(|value| value.clone())
                .unwrap_or_default(),
            namespace_id: self
                .status
                .namespace_id
                .lock()
                .map(|value| value.clone())
                .unwrap_or_default(),
            last_error: self.status.last_error.load(Ordering::Acquire),
        }
    }

    pub fn open_snapshot(&self, request_id: u64) -> NativeResult<(u64, u64)> {
        let prepared = self
            .snapshots
            .lock()
            .map_err(|_| NativeError::new(ErrorCode::Internal, "snapshot lock poisoned"))?
            .get(&request_id)
            .cloned()
            .ok_or_else(|| NativeError::new(ErrorCode::NotReady, "snapshot is not ready"))?;
        let current_epoch = self
            .events
            .lock()
            .map_err(|_| NativeError::new(ErrorCode::Internal, "event lock poisoned"))?
            .epoch();
        if prepared.epoch != current_epoch {
            return Err(NativeError::new(
                ErrorCode::SnapshotStale,
                "snapshot epoch is stale",
            ));
        }
        if prepared.created_at.elapsed() > self.config.max_snapshot_lifetime {
            return Err(NativeError::new(
                ErrorCode::SnapshotStale,
                "snapshot lifetime expired",
            ));
        }
        let handle = NEXT_SNAPSHOT_HANDLE.fetch_add(1, Ordering::Relaxed);
        let cursor = Arc::new(SnapshotCursor {
            prepared: prepared.clone(),
            cursor: Mutex::new(0),
            lifetime: self.config.max_snapshot_lifetime,
            events: self.events.clone(),
        });
        let mut handles = snapshot_handles()
            .lock()
            .map_err(|_| NativeError::new(ErrorCode::Internal, "snapshot handle lock poisoned"))?;
        if handles.len() >= MAX_OPEN_SNAPSHOT_HANDLES {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "maximum concurrent snapshot handles reached",
            ));
        }
        handles.insert(handle, cursor);
        Ok((handle, prepared.base_event_sequence))
    }

    pub fn poll_snapshot(handle: u64) -> NativeResult<Option<SnapshotRecord>> {
        let cursor = snapshot_handles()
            .lock()
            .map_err(|_| NativeError::new(ErrorCode::Internal, "snapshot handle lock poisoned"))?
            .get(&handle)
            .cloned()
            .ok_or_else(|| NativeError::new(ErrorCode::NotFound, "snapshot handle not found"))?;
        let current_epoch = cursor
            .events
            .lock()
            .map_err(|_| NativeError::new(ErrorCode::Internal, "event lock poisoned"))?
            .epoch();
        if current_epoch != cursor.prepared.epoch {
            return Err(NativeError::new(
                ErrorCode::SnapshotStale,
                "snapshot epoch is stale",
            ));
        }
        if cursor.prepared.created_at.elapsed() > cursor.lifetime {
            return Err(NativeError::new(
                ErrorCode::SnapshotStale,
                "snapshot lifetime expired",
            ));
        }
        let mut index = cursor
            .cursor
            .lock()
            .map_err(|_| NativeError::new(ErrorCode::Internal, "snapshot cursor lock poisoned"))?;
        let record = cursor.prepared.records.get(*index).cloned();
        if record.is_some() {
            *index = index.saturating_add(1);
        }
        Ok(record)
    }

    pub fn destroy_snapshot(handle: u64) {
        if let Ok(mut handles) = snapshot_handles().lock() {
            handles.remove(&handle);
        }
    }
}

async fn run_service(
    service: Arc<Service>,
    mut command_receiver: mpsc::Receiver<Command>,
    live_sender: mpsc::Sender<LiveInput>,
    mut live_receiver: mpsc::Receiver<LiveInput>,
) {
    let mut node = NativeNode::new(service.config.clone(), live_sender);
    loop {
        tokio::select! {
            command = command_receiver.recv() => {
                let Some(command) = command else { break; };
                service.status.pending_commands.fetch_sub(1, Ordering::Relaxed);
                if handle_command(&service, &mut node, command).await {
                    break;
                }
            }
            live = live_receiver.recv() => {
                let Some(live) = live else { continue; };
                match node.handle_live(live).await {
                    Ok(events) => {
                        for event in events { emit(&service, event); }
                        sync_status(&service, &node);
                    }
                    Err(error) => emit_error(&service, error),
                }
            }
        }
    }
    let lifecycle = service.status.lifecycle.load(Ordering::Acquire);
    if lifecycle != LIFE_CLOSED && lifecycle != LIFE_DESTROYED {
        service
            .status
            .lifecycle
            .store(LIFE_CLOSED, Ordering::Release);
    }
}

async fn handle_command(service: &Arc<Service>, node: &mut NativeNode, command: Command) -> bool {
    match command {
        Command::Start => match node.start().await {
            Ok(events) => {
                let _ = service.status.lifecycle.compare_exchange(
                    LIFE_STARTING,
                    LIFE_RUNNING,
                    Ordering::AcqRel,
                    Ordering::Acquire,
                );
                for event in events {
                    emit(service, event);
                }
                sync_status(service, node);
            }
            Err(error) => {
                emit_error(service, error);
                let _ = service.status.lifecycle.compare_exchange(
                    LIFE_STARTING,
                    LIFE_CLOSED,
                    Ordering::AcqRel,
                    Ordering::Acquire,
                );
            }
        },
        Command::CreateGroup => match node.create_group().await {
            Ok(events) => {
                for event in events {
                    emit(service, event);
                }
                sync_status(service, node);
            }
            Err(error) => emit_error(service, error),
        },
        Command::JoinGroup(ticket) => match node.join_group(ticket).await {
            Ok(events) => {
                for event in events {
                    emit(service, event);
                }
                sync_status(service, node);
            }
            Err(error) => emit_error(service, error),
        },
        Command::Leave => match node.leave().await {
            Ok(events) => {
                for event in events {
                    emit(service, event);
                }
                sync_status(service, node);
            }
            Err(error) => {
                emit_error(service, error);
                sync_status(service, node);
            }
        },
        Command::Put(put) => match node
            .put(
                put.key,
                put.record_id,
                put.record_type,
                put.generation,
                put.revision,
                put.payload,
            )
            .await
        {
            Ok(events) => {
                for event in events {
                    emit(service, event);
                }
            }
            Err(error) => emit_error(service, error),
        },
        Command::SetCharacterAuthor(character) => {
            match node.set_character_author(character).await {
                Ok(()) => {}
                Err(error) => emit_error(service, error),
            }
        }
        Command::RequestSnapshot(request_id) => match node.snapshot_records() {
            Ok(records) => {
                let (epoch, base_event_sequence) = service
                    .events
                    .lock()
                    .map(|events| (events.epoch(), events.latest_sequence()))
                    .unwrap_or((0, 0));
                let prepared = Arc::new(PreparedSnapshot {
                    epoch,
                    base_event_sequence,
                    records,
                    created_at: Instant::now(),
                });
                if let Ok(mut snapshots) = service.snapshots.lock() {
                    if snapshots.len() >= 8
                        && let Some(oldest) = snapshots.keys().next().copied()
                    {
                        snapshots.remove(&oldest);
                    }
                    snapshots.insert(request_id, prepared);
                }
                let mut event = NativeEvent::simple(EventKind::SnapshotReady);
                event.aux = request_id;
                emit(service, event);
            }
            Err(error) => emit_error(service, error),
        },
        Command::Shutdown { timeout_ms } => {
            match time::timeout(Duration::from_millis(timeout_ms as u64), node.shutdown()).await {
                Ok(Ok(events)) => {
                    for event in events {
                        emit(service, event);
                    }
                }
                Ok(Err(error)) => emit_error(service, error),
                Err(_) => {
                    let mut event = NativeEvent::simple(EventKind::Warning);
                    event.value = b"native shutdown cleanup deadline elapsed".to_vec();
                    emit(service, event);
                }
            }
            sync_status(service, node);
            if service.status.lifecycle.load(Ordering::Acquire) != LIFE_DESTROYED {
                service
                    .status
                    .lifecycle
                    .store(LIFE_CLOSED, Ordering::Release);
            }
            return true;
        }
    }
    false
}

fn sync_status(service: &Arc<Service>, node: &NativeNode) {
    service
        .status
        .joined
        .store(node.is_joined() as u32, Ordering::Release);
    if let Ok(mut endpoint_id) = service.status.endpoint_id.lock() {
        *endpoint_id = node.endpoint_id();
    }
    if let Ok(mut namespace_id) = service.status.namespace_id.lock() {
        *namespace_id = node.namespace_id();
    }
}

fn emit(service: &Arc<Service>, event: NativeEvent) {
    if let Ok(mut events) = service.events.lock() {
        events.push(event);
        service
            .status
            .world_epoch
            .store(events.epoch(), Ordering::Release);
        service
            .status
            .pending_events
            .store(events.len() as u64, Ordering::Release);
    }
}

fn emit_error(service: &Arc<Service>, error: NativeError) {
    let error_id = service.errors.insert(error.message.clone());
    service.status.last_error.store(error_id, Ordering::Release);
    let mut event = NativeEvent::simple(EventKind::Error);
    event.value = error.message.into_bytes();
    event.aux = ((error.code.as_u32() as u64) << 32) | error_id;
    emit(service, event);
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_service(event_capacity: usize) -> Service {
        let (command_sender, _command_receiver) = mpsc::channel::<Command>(1);
        Service {
            command_sender,
            command_gate: Mutex::new(()),
            events: Arc::new(Mutex::new(EventQueue::new(event_capacity))),
            errors: global_errors(),
            status: Arc::new(StatusState::new()),
            snapshots: Arc::new(Mutex::new(BTreeMap::new())),
            config: NativeConfig::default(),
        }
    }

    #[test]
    fn snapshot_open_rejects_a_stale_event_epoch() {
        let service = test_service(2);
        service.snapshots.lock().expect("snapshot lock").insert(
            17,
            Arc::new(PreparedSnapshot {
                epoch: 0,
                base_event_sequence: 0,
                records: Vec::new(),
                created_at: Instant::now(),
            }),
        );
        let mut first = NativeEvent::simple(EventKind::RecordInserted);
        first.key = b"first".to_vec();
        service.events.lock().expect("event lock").push(first);
        let mut second = NativeEvent::simple(EventKind::RecordInserted);
        second.key = b"second".to_vec();
        service.events.lock().expect("event lock").push(second);
        let mut third = NativeEvent::simple(EventKind::RecordInserted);
        third.key = b"third".to_vec();
        service.events.lock().expect("event lock").push(third);

        let error = service
            .open_snapshot(17)
            .expect_err("epoch overflow must stale an old snapshot");
        assert_eq!(error.code, ErrorCode::SnapshotStale);
    }

    #[test]
    fn expired_snapshot_poll_is_retryable_and_destroy_is_idempotent() {
        let handle = NEXT_SNAPSHOT_HANDLE.fetch_add(1, Ordering::Relaxed);
        let prepared = Arc::new(PreparedSnapshot {
            epoch: 0,
            base_event_sequence: 0,
            records: Vec::new(),
            created_at: Instant::now()
                .checked_sub(Duration::from_secs(1))
                .expect("test instant should be representable"),
        });
        snapshot_handles()
            .lock()
            .expect("snapshot handle lock")
            .insert(
                handle,
                Arc::new(SnapshotCursor {
                    prepared,
                    cursor: Mutex::new(0),
                    lifetime: Duration::ZERO,
                    events: Arc::new(Mutex::new(EventQueue::new(2))),
                }),
            );
        let error = Service::poll_snapshot(handle)
            .expect_err("expired snapshot must report a retryable stale result");
        assert_eq!(error.code, ErrorCode::SnapshotStale);
        Service::destroy_snapshot(handle);
        Service::destroy_snapshot(handle);
    }

    #[test]
    fn snapshot_poll_rejects_overflow_after_open() {
        let handle = NEXT_SNAPSHOT_HANDLE.fetch_add(1, Ordering::Relaxed);
        let events = Arc::new(Mutex::new(EventQueue::new(2)));
        snapshot_handles()
            .lock()
            .expect("snapshot handle lock")
            .insert(
                handle,
                Arc::new(SnapshotCursor {
                    prepared: Arc::new(PreparedSnapshot {
                        epoch: 0,
                        base_event_sequence: 0,
                        records: Vec::new(),
                        created_at: Instant::now(),
                    }),
                    cursor: Mutex::new(0),
                    lifetime: Duration::from_secs(60),
                    events: events.clone(),
                }),
            );
        for key in [b"one".to_vec(), b"two".to_vec(), b"three".to_vec()] {
            let mut event = NativeEvent::simple(EventKind::RecordInserted);
            event.key = key;
            events.lock().expect("event lock").push(event);
        }
        let error = Service::poll_snapshot(handle)
            .expect_err("overflow after open must stale the snapshot cursor");
        assert_eq!(error.code, ErrorCode::SnapshotStale);
        Service::destroy_snapshot(handle);
    }
}
