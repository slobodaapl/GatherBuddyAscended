#![allow(non_camel_case_types)]
#![allow(clippy::too_many_arguments)]

use serde::{
    Deserialize,
    de::{self, Deserializer, MapAccess, Visitor},
};
use std::{
    collections::BTreeMap,
    fmt,
    mem::size_of,
    panic::{AssertUnwindSafe, catch_unwind},
    slice,
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicU64, Ordering},
    },
};

use crate::{
    document::{MAX_RECORD_TYPE_BYTES, parse_record_id},
    error::{ErrorCode, NativeError, NativeResult},
    events::{EventKind, NativeEvent},
    node::NativeConfig,
    runtime::{Command, PutCommand, Service, global_errors},
};

pub const GBM_ABI_VERSION: u32 = 2;
pub const GBM_CONFIG_SCHEMA_VERSION: u32 = 1;
pub const GBM_EVENT_SCHEMA_VERSION: u32 = 1;

pub const GBM_E_OK: u32 = ErrorCode::Ok as u32;
pub const GBM_E_INVALID_ARGUMENT: u32 = ErrorCode::InvalidArgument as u32;
pub const GBM_E_ABI_MISMATCH: u32 = ErrorCode::AbiMismatch as u32;
pub const GBM_E_INVALID_HANDLE: u32 = ErrorCode::InvalidHandle as u32;
pub const GBM_E_INVALID_STATE: u32 = ErrorCode::InvalidState as u32;
pub const GBM_E_QUEUE_FULL: u32 = ErrorCode::QueueFull as u32;
pub const GBM_E_NO_EVENT: u32 = ErrorCode::NoEvent as u32;
pub const GBM_E_NOT_READY: u32 = ErrorCode::NotReady as u32;
pub const GBM_E_CLOSING: u32 = ErrorCode::Closing as u32;
pub const GBM_E_ALREADY_DESTROYED: u32 = ErrorCode::AlreadyDestroyed as u32;
pub const GBM_E_NOT_FOUND: u32 = ErrorCode::NotFound as u32;
pub const GBM_E_LIMIT_EXCEEDED: u32 = ErrorCode::LimitExceeded as u32;
pub const GBM_E_INVALID_RECORD: u32 = ErrorCode::InvalidRecord as u32;
pub const GBM_E_CLOCK_DRIFT: u32 = ErrorCode::ClockDrift as u32;
pub const GBM_E_STORAGE: u32 = ErrorCode::Storage as u32;
pub const GBM_E_NETWORK: u32 = ErrorCode::Network as u32;
pub const GBM_E_PANIC: u32 = ErrorCode::Panic as u32;
pub const GBM_E_INTERNAL: u32 = ErrorCode::Internal as u32;
pub const GBM_E_SNAPSHOT_STALE: u32 = ErrorCode::SnapshotStale as u32;

pub const GBM_EVENT_STARTED: u32 = EventKind::Started as u32;
pub const GBM_EVENT_STOPPED: u32 = EventKind::Stopped as u32;
pub const GBM_EVENT_JOINING: u32 = EventKind::Joining as u32;
pub const GBM_EVENT_JOINED: u32 = EventKind::Joined as u32;
pub const GBM_EVENT_LEFT: u32 = EventKind::Left as u32;
pub const GBM_EVENT_RECORD_INSERTED: u32 = EventKind::RecordInserted as u32;
pub const GBM_EVENT_RECORD_REMOVED: u32 = EventKind::RecordRemoved as u32;
pub const GBM_EVENT_INITIAL_SYNC_COMPLETED: u32 = EventKind::InitialSyncCompleted as u32;
pub const GBM_EVENT_PEER_CONNECTED: u32 = EventKind::PeerConnected as u32;
pub const GBM_EVENT_PEER_DISCONNECTED: u32 = EventKind::PeerDisconnected as u32;
pub const GBM_EVENT_PATH_CHANGED: u32 = EventKind::PathChanged as u32;
pub const GBM_EVENT_WARNING: u32 = EventKind::Warning as u32;
pub const GBM_EVENT_ERROR: u32 = EventKind::Error as u32;
pub const GBM_EVENT_WORLD_INVALIDATED: u32 = EventKind::WorldInvalidated as u32;
pub const GBM_EVENT_SNAPSHOT_READY: u32 = EventKind::SnapshotReady as u32;

pub const GBM_LIFECYCLE_CREATED: u32 = 1;
pub const GBM_LIFECYCLE_STARTING: u32 = 2;
pub const GBM_LIFECYCLE_RUNNING: u32 = 3;
pub const GBM_LIFECYCLE_CLOSING: u32 = 4;
pub const GBM_LIFECYCLE_CLOSED: u32 = 5;
pub const GBM_LIFECYCLE_DESTROYED: u32 = 6;

pub type gbm_handle = u64;
pub type gbm_snapshot_handle = u64;

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct gbm_result {
    pub code: u32,
    pub flags: u32,
    pub error_id: u64,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct gbm_buffer {
    pub ptr: *mut u8,
    pub len: usize,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct gbm_event {
    pub struct_size: u32,
    /// Application envelope protocol version carried by `value`.
    pub protocol_version: u32,
    pub kind: u32,
    pub sequence: u64,
    pub world_epoch: u64,
    pub key: gbm_buffer,
    pub value: gbm_buffer,
    pub actual_author: gbm_buffer,
    pub content_hash: gbm_buffer,
    pub aux: u64,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct gbm_record {
    pub struct_size: u32,
    /// Application envelope protocol version carried by `value`.
    pub protocol_version: u32,
    pub flags: u32,
    pub key: gbm_buffer,
    pub value: gbm_buffer,
    pub actual_author: gbm_buffer,
    pub content_hash: gbm_buffer,
    pub generation: u64,
    pub has_generation: u32,
    pub revision: u64,
    pub record_type: gbm_buffer,
    pub hlc_physical_unix_ms: i64,
    pub hlc_logical: u64,
    pub hlc_node_id: gbm_buffer,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct gbm_status {
    pub struct_size: u32,
    pub lifecycle: u32,
    pub flags: u32,
    pub joined: u32,
    pub pending_commands: u64,
    pub pending_events: u64,
    pub world_epoch: u64,
    pub endpoint_id: gbm_buffer,
    pub namespace_id: gbm_buffer,
    pub last_error_id: u64,
}

const MAX_CONFIG_JSON_BYTES: usize = 64 * 1024;
const MAX_CONFIG_RELAY_URLS: usize = 128;
const MAX_CONFIG_RELAY_URL_BYTES: usize = 4096;

#[derive(Debug)]
struct ConfigJson {
    schema_version: u32,
    storage_directory: String,
    event_capacity: u64,
    command_capacity: u64,
    max_key_bytes: u64,
    max_value_bytes: u64,
    relay_mode: u32,
    relay_urls: Vec<String>,
}

impl<'de> Deserialize<'de> for ConfigJson {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        struct ConfigVisitor;

        impl<'de> Visitor<'de> for ConfigVisitor {
            type Value = ConfigJson;

            fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str("an exact GatherBuddy mesh configuration object")
            }

            fn visit_map<A>(self, mut map: A) -> Result<Self::Value, A::Error>
            where
                A: MapAccess<'de>,
            {
                let mut schema_version = None;
                let mut storage_directory = None;
                let mut event_capacity = None;
                let mut command_capacity = None;
                let mut max_key_bytes = None;
                let mut max_value_bytes = None;
                let mut relay_mode = None;
                let mut relay_urls = None;
                while let Some(field) = map.next_key::<String>()? {
                    match field.as_str() {
                        "schema_version" => {
                            if schema_version.is_some() {
                                return Err(de::Error::duplicate_field("schema_version"));
                            }
                            schema_version = Some(map.next_value()?);
                        }
                        "storage_directory" => {
                            if storage_directory.is_some() {
                                return Err(de::Error::duplicate_field("storage_directory"));
                            }
                            storage_directory = Some(map.next_value()?);
                        }
                        "event_capacity" => {
                            if event_capacity.is_some() {
                                return Err(de::Error::duplicate_field("event_capacity"));
                            }
                            event_capacity = Some(map.next_value()?);
                        }
                        "command_capacity" => {
                            if command_capacity.is_some() {
                                return Err(de::Error::duplicate_field("command_capacity"));
                            }
                            command_capacity = Some(map.next_value()?);
                        }
                        "max_key_bytes" => {
                            if max_key_bytes.is_some() {
                                return Err(de::Error::duplicate_field("max_key_bytes"));
                            }
                            max_key_bytes = Some(map.next_value()?);
                        }
                        "max_value_bytes" => {
                            if max_value_bytes.is_some() {
                                return Err(de::Error::duplicate_field("max_value_bytes"));
                            }
                            max_value_bytes = Some(map.next_value()?);
                        }
                        "relay_mode" => {
                            if relay_mode.is_some() {
                                return Err(de::Error::duplicate_field("relay_mode"));
                            }
                            relay_mode = Some(map.next_value()?);
                        }
                        "relay_urls" => {
                            if relay_urls.is_some() {
                                return Err(de::Error::duplicate_field("relay_urls"));
                            }
                            relay_urls = Some(map.next_value()?);
                        }
                        other => return Err(de::Error::unknown_field(other, CONFIG_FIELDS)),
                    }
                }
                Ok(ConfigJson {
                    schema_version: schema_version
                        .ok_or_else(|| de::Error::missing_field("schema_version"))?,
                    storage_directory: storage_directory
                        .ok_or_else(|| de::Error::missing_field("storage_directory"))?,
                    event_capacity: event_capacity
                        .ok_or_else(|| de::Error::missing_field("event_capacity"))?,
                    command_capacity: command_capacity
                        .ok_or_else(|| de::Error::missing_field("command_capacity"))?,
                    max_key_bytes: max_key_bytes
                        .ok_or_else(|| de::Error::missing_field("max_key_bytes"))?,
                    max_value_bytes: max_value_bytes
                        .ok_or_else(|| de::Error::missing_field("max_value_bytes"))?,
                    relay_mode: relay_mode.ok_or_else(|| de::Error::missing_field("relay_mode"))?,
                    relay_urls: relay_urls.ok_or_else(|| de::Error::missing_field("relay_urls"))?,
                })
            }
        }

        deserializer.deserialize_map(ConfigVisitor)
    }
}

const CONFIG_FIELDS: &[&str] = &[
    "schema_version",
    "storage_directory",
    "event_capacity",
    "command_capacity",
    "max_key_bytes",
    "max_value_bytes",
    "relay_mode",
    "relay_urls",
];

static NEXT_HANDLE: AtomicU64 = AtomicU64::new(1);
static SERVICES: OnceLock<Mutex<BTreeMap<gbm_handle, Arc<Service>>>> = OnceLock::new();

fn services() -> &'static Mutex<BTreeMap<gbm_handle, Arc<Service>>> {
    SERVICES.get_or_init(|| Mutex::new(BTreeMap::new()))
}

fn next_handle() -> NativeResult<gbm_handle> {
    let mut current = NEXT_HANDLE.load(Ordering::Relaxed);
    loop {
        if current == 0 {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "native handle space is exhausted",
            ));
        }
        let next = current.checked_add(1).unwrap_or(0);
        match NEXT_HANDLE.compare_exchange_weak(current, next, Ordering::Relaxed, Ordering::Relaxed)
        {
            Ok(_) => return Ok(current),
            Err(observed) => current = observed,
        }
    }
}

fn ok() -> gbm_result {
    gbm_result {
        code: ErrorCode::Ok.as_u32(),
        flags: 0,
        error_id: 0,
    }
}

fn error_result(error: NativeError) -> gbm_result {
    let error_id = global_errors().insert(error.message);
    gbm_result {
        code: error.code.as_u32(),
        flags: 0,
        error_id,
    }
}

fn panic_result() -> gbm_result {
    error_result(NativeError::new(
        ErrorCode::Panic,
        "panic contained at native ABI boundary",
    ))
}

fn guarded(function: impl FnOnce() -> NativeResult<()>) -> gbm_result {
    match catch_unwind(AssertUnwindSafe(function)) {
        Ok(Ok(())) => ok(),
        Ok(Err(error)) => error_result(error),
        Err(_) => panic_result(),
    }
}

fn resolve(handle: gbm_handle) -> NativeResult<Arc<Service>> {
    if handle == 0 {
        return Err(NativeError::new(
            ErrorCode::InvalidHandle,
            "zero is not a valid handle",
        ));
    }
    services()
        .lock()
        .map_err(|_| NativeError::new(ErrorCode::Internal, "service registry lock poisoned"))?
        .get(&handle)
        .cloned()
        .ok_or_else(|| NativeError::new(ErrorCode::InvalidHandle, "native handle not found"))
}

/// # Safety
/// The caller must provide a pointer valid for reads of `len` bytes. Null is accepted only when
/// `len` is zero. The returned vector owns a copy and never aliases caller memory.
unsafe fn copy_input(ptr: *const u8, len: usize, max: usize) -> NativeResult<Vec<u8>> {
    if len > max {
        return Err(NativeError::new(
            ErrorCode::LimitExceeded,
            "ABI input exceeds configured limit",
        ));
    }
    if len == 0 {
        return Ok(Vec::new());
    }
    if ptr.is_null() {
        return Err(NativeError::invalid(
            "nonzero ABI length requires a non-null pointer",
        ));
    }
    // SAFETY: validated non-null and caller-owned length is bounded above.
    Ok(unsafe { slice::from_raw_parts(ptr, len) }.to_vec())
}

/// # Safety
/// `buffer` must contain a pointer/length pair allocated by this library, or a null pointer with
/// zero length. The allocation is released exactly once.
unsafe fn release_buffer(buffer: gbm_buffer) {
    if buffer.ptr.is_null() {
        return;
    }
    if buffer.len == 0 {
        return;
    }
    // SAFETY: buffers are allocated as boxed byte slices by `owned_buffer`.
    let slice_ptr = std::ptr::slice_from_raw_parts_mut(buffer.ptr, buffer.len);
    // SAFETY: ownership is transferred back exactly once by the ABI caller.
    drop(unsafe { Box::from_raw(slice_ptr) });
}

fn owned_buffer(mut bytes: Vec<u8>) -> gbm_buffer {
    if bytes.is_empty() {
        return gbm_buffer::default();
    }
    bytes.shrink_to_fit();
    let len = bytes.len();
    let ptr = Box::into_raw(bytes.into_boxed_slice()) as *mut u8;
    gbm_buffer { ptr, len }
}

fn config_from_json(bytes: &[u8]) -> NativeResult<NativeConfig> {
    if bytes.is_empty() {
        return Err(NativeError::invalid("config JSON is required"));
    }
    let config: ConfigJson = serde_json::from_slice(bytes)
        .map_err(|error| NativeError::invalid(format!("invalid config JSON: {error}")))?;
    if config.schema_version != GBM_CONFIG_SCHEMA_VERSION {
        return Err(NativeError::new(
            ErrorCode::AbiMismatch,
            "unsupported config schema",
        ));
    }
    if config.storage_directory.is_empty()
        || config.storage_directory.len() > 4096
        || config.storage_directory.contains('\0')
    {
        return Err(NativeError::new(
            ErrorCode::LimitExceeded,
            "storage_directory exceeds native limits",
        ));
    }
    if config.relay_urls.len() > MAX_CONFIG_RELAY_URLS
        || config.relay_urls.iter().any(|url| {
            url.is_empty() || url.len() > MAX_CONFIG_RELAY_URL_BYTES || url.contains('\0')
        })
    {
        return Err(NativeError::new(
            ErrorCode::LimitExceeded,
            "relay URL configuration exceeds native limits",
        ));
    }
    if config.relay_mode > 1 {
        return Err(NativeError::invalid("unsupported relay_mode"));
    }
    let defaults = NativeConfig::default();
    let max_key_bytes = usize::try_from(config.max_key_bytes)
        .map_err(|_| NativeError::new(ErrorCode::LimitExceeded, "max_key_bytes overflows usize"))?;
    let max_value_bytes = usize::try_from(config.max_value_bytes).map_err(|_| {
        NativeError::new(ErrorCode::LimitExceeded, "max_value_bytes overflows usize")
    })?;
    let event_capacity = usize::try_from(config.event_capacity).map_err(|_| {
        NativeError::new(ErrorCode::LimitExceeded, "event_capacity overflows usize")
    })?;
    let command_capacity = usize::try_from(config.command_capacity).map_err(|_| {
        NativeError::new(ErrorCode::LimitExceeded, "command_capacity overflows usize")
    })?;
    if event_capacity == 0
        || command_capacity == 0
        || event_capacity > 1_000_000
        || command_capacity > 65_536
    {
        return Err(NativeError::new(
            ErrorCode::LimitExceeded,
            "configured queue capacity exceeds native hard limit",
        ));
    }
    if max_key_bytes == 0
        || max_value_bytes == 0
        || max_key_bytes > 4096
        || max_value_bytes > 16 * 1024 * 1024
    {
        return Err(NativeError::new(
            ErrorCode::LimitExceeded,
            "configured key or value limit exceeds native hard limit",
        ));
    }
    Ok(NativeConfig {
        storage_directory: std::path::PathBuf::from(config.storage_directory),
        event_capacity,
        command_capacity,
        max_key_bytes,
        max_value_bytes,
        relay_mode: config.relay_mode,
        relay_urls: config.relay_urls,
        max_hlc_delta_ms: defaults.max_hlc_delta_ms,
        max_snapshot_records: defaults.max_snapshot_records,
        max_snapshot_lifetime: defaults.max_snapshot_lifetime,
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_abi_version() -> u32 {
    catch_unwind(AssertUnwindSafe(|| GBM_ABI_VERSION)).unwrap_or_default()
}

/// Creates a service handle from the caller-owned versioned JSON configuration.
///
/// # Safety
/// `config_json` may be null only when `config_len` is zero; otherwise it must point to
/// `config_len` readable bytes that remain valid for the duration of this call. `out_handle` may
/// be null to receive `GBM_E_INVALID_ARGUMENT`; otherwise it must point to writable `gbm_handle`
/// storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_create(
    config_json: *const u8,
    config_len: usize,
    out_handle: *mut gbm_handle,
) -> gbm_result {
    guarded(|| {
        if out_handle.is_null() {
            return Err(NativeError::invalid("out_handle is null"));
        }
        let config_json = unsafe { copy_input(config_json, config_len, MAX_CONFIG_JSON_BYTES) }?;
        let config = config_from_json(&config_json)?;
        let handle = next_handle()?;
        let service = Service::new(config);
        services()
            .lock()
            .map_err(|_| NativeError::new(ErrorCode::Internal, "service registry lock poisoned"))?
            .insert(handle, service);
        // SAFETY: out_handle was validated non-null and points to caller-owned writable storage.
        unsafe { *out_handle = handle };
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_start(handle: gbm_handle) -> gbm_result {
    guarded(|| resolve(handle)?.start())
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_create_group(handle: gbm_handle) -> gbm_result {
    guarded(|| resolve(handle)?.try_send(Command::CreateGroup))
}

/// Queues a group-ticket import.
///
/// # Safety
/// `ticket` may be null only when `ticket_len` is zero; otherwise it must point to `ticket_len`
/// readable bytes that remain valid for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_join_group(
    handle: gbm_handle,
    ticket: *const u8,
    ticket_len: usize,
) -> gbm_result {
    guarded(|| {
        // SAFETY: the ABI caller owns the ticket bytes for the duration of this call.
        let ticket = unsafe { copy_input(ticket, ticket_len, 64 * 1024) }?;
        resolve(handle)?.try_send(Command::JoinGroup(ticket))
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_leave_group(handle: gbm_handle) -> gbm_result {
    guarded(|| resolve(handle)?.try_send(Command::Leave))
}

/// Queues an opaque application record for native HLC stamping and replication.
///
/// # Safety
/// `record_id`, `key`, `record_type`, and `payload` may be null only when their corresponding
/// lengths are zero; non-empty buffers must point to readable bytes that remain valid for the
/// duration of this call. `record_id` must be canonical lowercase GUID-D text.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_put(
    handle: gbm_handle,
    record_id: *const u8,
    record_id_len: usize,
    key: *const u8,
    key_len: usize,
    record_type: *const u8,
    record_type_len: usize,
    has_generation: u8,
    generation: u64,
    revision: u64,
    payload: *const u8,
    payload_len: usize,
) -> gbm_result {
    guarded(|| {
        if has_generation > 1 {
            return Err(NativeError::invalid("has_generation must be zero or one"));
        }
        let service = resolve(handle)?;
        // SAFETY: pointers are copied and validated before command ownership is transferred.
        let record_id = unsafe { copy_input(record_id, record_id_len, 36) }?;
        let record_id = std::str::from_utf8(&record_id)
            .map_err(|_| NativeError::invalid("record id must be UTF-8"))?;
        let record_id = parse_record_id(record_id)?;
        let key = unsafe { copy_input(key, key_len, service_config_limit(&service, true)) }?;
        let record_type =
            unsafe { copy_input(record_type, record_type_len, MAX_RECORD_TYPE_BYTES) }?;
        let record_type = std::str::from_utf8(&record_type)
            .map_err(|_| NativeError::invalid("record type must be UTF-8"))?
            .to_owned();
        if record_type.is_empty() {
            return Err(NativeError::invalid("record type cannot be empty"));
        }
        let payload =
            unsafe { copy_input(payload, payload_len, service_config_limit(&service, false)) }?;
        service.try_send(Command::Put(PutCommand {
            key,
            record_id,
            record_type,
            generation: (has_generation != 0).then_some(generation),
            revision,
            payload,
        }))
    })
}

fn service_config_limit(_service: &Service, key: bool) -> usize {
    // The command is copied before it reaches the worker. NativeConfig performs the same check
    // again at the authoritative write boundary; these conservative ABI limits prevent an
    // oversized allocation before the service can inspect its config.
    if key { 4096 } else { 16 * 1024 * 1024 }
}

/// Polls one prepared event and transfers ownership of its buffers to the caller.
///
/// # Safety
/// `out_event` may be null to receive `GBM_E_INVALID_ARGUMENT`; otherwise it must point to
/// writable `gbm_event` storage for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_poll_event(
    handle: gbm_handle,
    out_event: *mut gbm_event,
) -> gbm_result {
    guarded(|| {
        if out_event.is_null() {
            return Err(NativeError::invalid("out_event is null"));
        }
        let service = resolve(handle)?;
        let Some(event) = service.poll_event() else {
            return Err(NativeError::new(ErrorCode::NoEvent, "event queue is empty"));
        };
        // SAFETY: out_event is non-null writable caller memory; each owned buffer is allocated by
        // this library and released by gbm_event_free.
        unsafe {
            *out_event = event_to_ffi(event);
        }
        Ok(())
    })
}

fn event_to_ffi(event: NativeEvent) -> gbm_event {
    gbm_event {
        struct_size: size_of::<gbm_event>() as u32,
        protocol_version: event.protocol_version as u32,
        kind: event.kind as u32,
        sequence: event.sequence,
        world_epoch: event.world_epoch,
        key: owned_buffer(event.key),
        value: owned_buffer(event.value),
        actual_author: owned_buffer(event.actual_author),
        content_hash: owned_buffer(event.content_hash),
        aux: event.aux,
    }
}

/// Reads current service status and transfers ownership of status buffers to the caller.
///
/// # Safety
/// `out_status` may be null to receive `GBM_E_INVALID_ARGUMENT`; otherwise it must point to
/// writable `gbm_status` storage for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_get_status(
    handle: gbm_handle,
    out_status: *mut gbm_status,
) -> gbm_result {
    guarded(|| {
        if out_status.is_null() {
            return Err(NativeError::invalid("out_status is null"));
        }
        let status = resolve(handle)?.status();
        // SAFETY: caller supplied writable gbm_status storage and receives owned buffers.
        unsafe {
            *out_status = gbm_status {
                struct_size: size_of::<gbm_status>() as u32,
                lifecycle: status.lifecycle,
                flags: 0,
                joined: status.joined as u32,
                pending_commands: status.pending_commands,
                pending_events: status.pending_events,
                world_epoch: status.world_epoch,
                endpoint_id: owned_buffer(status.endpoint_id),
                namespace_id: owned_buffer(status.namespace_id),
                last_error_id: status.last_error,
            };
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_shutdown(handle: gbm_handle, timeout_ms: u32) -> gbm_result {
    guarded(|| resolve(handle)?.shutdown(timeout_ms))
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_destroy(handle: gbm_handle) -> gbm_result {
    guarded(|| {
        if handle == 0 {
            return Err(NativeError::new(
                ErrorCode::InvalidHandle,
                "zero is not a valid handle",
            ));
        }
        let service = services()
            .lock()
            .map_err(|_| NativeError::new(ErrorCode::Internal, "service registry lock poisoned"))?
            .remove(&handle);
        if let Some(service) = service {
            service.mark_destroyed();
        }
        Ok(())
    })
}

/// Selects the persistent per-character author key.
///
/// # Safety
/// `key` may be null only when `len` is zero; otherwise it must point to `len` readable bytes that
/// remain valid for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_set_character_author(
    handle: gbm_handle,
    key: *const u8,
    len: usize,
) -> gbm_result {
    guarded(|| {
        let service = resolve(handle)?;
        let key = unsafe { copy_input(key, len, 4096) }?;
        service.try_send(Command::SetCharacterAuthor(key))
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_request_snapshot(handle: gbm_handle, request_id: u64) -> gbm_result {
    guarded(|| resolve(handle)?.try_send(Command::RequestSnapshot(request_id)))
}

/// Opens a prepared snapshot cursor.
///
/// # Safety
/// Each output pointer may be null to receive `GBM_E_INVALID_ARGUMENT`; otherwise it must point to
/// writable storage for the corresponding fixed-width result for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_snapshot_open(
    handle: gbm_handle,
    request_id: u64,
    out_snapshot: *mut gbm_snapshot_handle,
    out_base_event_sequence: *mut u64,
) -> gbm_result {
    guarded(|| {
        if out_snapshot.is_null() || out_base_event_sequence.is_null() {
            return Err(NativeError::invalid("snapshot output pointer is null"));
        }
        let service = resolve(handle)?;
        let (snapshot, base_sequence) = service.open_snapshot(request_id)?;
        // SAFETY: both pointers were checked non-null and point to caller-owned writable storage.
        unsafe {
            *out_snapshot = snapshot;
            *out_base_event_sequence = base_sequence;
        }
        Ok(())
    })
}

/// Polls one snapshot record and transfers ownership of its buffers to the caller.
///
/// # Safety
/// `out_record` and `out_done` may be null to receive `GBM_E_INVALID_ARGUMENT`; otherwise they
/// must point to writable storage for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_snapshot_poll(
    snapshot: gbm_snapshot_handle,
    out_record: *mut gbm_record,
    out_done: *mut u8,
) -> gbm_result {
    guarded(|| {
        if out_record.is_null() || out_done.is_null() {
            return Err(NativeError::invalid("snapshot output pointer is null"));
        }
        let record = Service::poll_snapshot(snapshot)?;
        // SAFETY: pointers were checked non-null; returned buffers are owned by the caller.
        unsafe {
            if let Some(record) = record {
                *out_done = 0;
                *out_record = record_to_ffi(record);
            } else {
                *out_done = 1;
                *out_record = gbm_record {
                    struct_size: size_of::<gbm_record>() as u32,
                    ..gbm_record::default()
                };
            }
        }
        Ok(())
    })
}

fn record_to_ffi(record: crate::events::SnapshotRecord) -> gbm_record {
    gbm_record {
        struct_size: size_of::<gbm_record>() as u32,
        protocol_version: record.protocol_version as u32,
        flags: 0,
        key: owned_buffer(record.key),
        value: owned_buffer(record.value),
        actual_author: owned_buffer(record.actual_author),
        content_hash: owned_buffer(record.content_hash),
        generation: record.generation.unwrap_or_default(),
        has_generation: record.generation.is_some() as u32,
        revision: record.revision,
        record_type: owned_buffer(record.record_type.into_bytes()),
        hlc_physical_unix_ms: record.hlc.physical_unix_ms,
        hlc_logical: record.hlc.logical,
        hlc_node_id: owned_buffer(record.hlc.node_id.to_vec()),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_snapshot_destroy(snapshot: gbm_snapshot_handle) -> gbm_result {
    guarded(|| {
        Service::destroy_snapshot(snapshot);
        Ok(())
    })
}

/// Returns an owned diagnostic buffer for an error identifier.
///
/// # Safety
/// `out` may be null to receive `GBM_E_INVALID_ARGUMENT`; otherwise it must point to writable
/// `gbm_buffer` storage for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn gbm_error_message(error_id: u64, out: *mut gbm_buffer) -> gbm_result {
    guarded(|| {
        if out.is_null() {
            return Err(NativeError::invalid("out error buffer is null"));
        }
        let bytes = global_errors()
            .get(error_id)
            .ok_or_else(|| NativeError::new(ErrorCode::NotFound, "error id not found"))?;
        // SAFETY: out is non-null writable caller memory and receives an owned buffer.
        unsafe {
            *out = owned_buffer(bytes);
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_error_free(error_id: u64) {
    let _ = catch_unwind(AssertUnwindSafe(|| global_errors().remove(error_id)));
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_buffer_free(buffer: gbm_buffer) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller promises the buffer was allocated by this library.
        unsafe { release_buffer(buffer) };
    }));
}

#[unsafe(no_mangle)]
pub extern "C" fn gbm_event_free(event: gbm_event) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: all fields came from event_to_ffi and are independently owned.
        unsafe {
            release_buffer(event.key);
            release_buffer(event.value);
            release_buffer(event.actual_author);
            release_buffer(event.content_hash);
        }
    }));
}
