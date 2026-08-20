use std::{cmp::Ordering, convert::TryFrom};

use base64::{Engine as _, engine::general_purpose::STANDARD as BASE64};
use iroh::{SecretKey, Signature};
use iroh_docs::{Author, AuthorId, AuthorPublicKey};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use uhlc::{HLC, ID, NTP64, Timestamp};

use crate::error::{ErrorCode, NativeError, NativeResult};

pub const PROTOCOL_VERSION: u16 = 1;
pub const MAX_NODE_ID_BYTES: usize = ID::MAX_SIZE;
pub const SIGNATURE_BYTES: usize = 64;
pub const MAX_RECORD_TYPE_BYTES: usize = 128;
pub const MAX_KEY_BYTES: usize = 4096;
pub const RECORD_ID_BYTES: usize = 16;
pub const ACTUAL_AUTHOR_ID_BYTES: usize = 32;
pub const SIGNATURE_ALGORITHM: &str = "iroh-docs-ed25519";

/// Wire representation of the application HLC.
///
/// `physical_unix_ms` is the Unix-millisecond projection of uhlc's NTP64
/// time. `raw` and `logical` are the same authoritative NTP64 ordering value;
/// the JSON wire form exposes `logical` and the padded 16-byte HLC node id;
/// `raw` remains an internal compatibility field for uhlc and persisted state.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
pub struct HlcWire {
    pub physical_unix_ms: i64,
    pub logical: u64,
    pub node_id: [u8; MAX_NODE_ID_BYTES],
    pub raw: u64,
}

impl HlcWire {
    pub fn from_timestamp(timestamp: &Timestamp) -> Self {
        let time = *timestamp.get_time();
        let raw = time.as_u64();
        let millis = (time.as_secs() as u128)
            .saturating_mul(1_000)
            .saturating_add((time.subsec_nanos() as u128) / 1_000_000);
        let physical_unix_ms = i64::try_from(millis).unwrap_or(i64::MAX);
        Self {
            physical_unix_ms,
            // `logical` intentionally carries the complete NTP64 value. A
            // millisecond-only counter would lose HLC ordering information.
            logical: raw,
            node_id: timestamp.get_id().to_le_bytes(),
            raw,
        }
    }

    pub fn to_timestamp(&self) -> NativeResult<Timestamp> {
        if self.logical != self.raw {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "HLC logical value does not match its authoritative NTP64 value",
            ));
        }
        let id = ID::try_from(&self.node_id).map_err(|error| {
            NativeError::new(
                ErrorCode::InvalidRecord,
                format!("invalid HLC node id: {error}"),
            )
        })?;
        Ok(Timestamp::new(NTP64(self.raw), id))
    }

    pub fn ordering_key(&self) -> (i64, u64, [u8; MAX_NODE_ID_BYTES]) {
        (self.physical_unix_ms, self.logical, self.node_id)
    }
}

impl Ord for HlcWire {
    fn cmp(&self, other: &Self) -> Ordering {
        self.ordering_key().cmp(&other.ordering_key())
    }
}

impl PartialOrd for HlcWire {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

fn decode_lower_hex<const N: usize>(value: &str, field: &str) -> NativeResult<[u8; N]> {
    if value.len() != N * 2 || value.bytes().any(|byte| !byte.is_ascii_hexdigit()) {
        return Err(NativeError::new(
            ErrorCode::InvalidRecord,
            format!(
                "{field} must be exactly {} lowercase hexadecimal characters",
                N * 2
            ),
        ));
    }
    if value.bytes().any(|byte| byte.is_ascii_uppercase()) {
        return Err(NativeError::new(
            ErrorCode::InvalidRecord,
            format!("{field} must use lowercase hexadecimal characters"),
        ));
    }
    let decoded = hex::decode(value).map_err(|_| {
        NativeError::new(
            ErrorCode::InvalidRecord,
            format!("{field} is not valid hexadecimal"),
        )
    })?;
    <[u8; N]>::try_from(decoded.as_slice()).map_err(|_| {
        NativeError::new(
            ErrorCode::InvalidRecord,
            format!("{field} has an invalid decoded length"),
        )
    })
}

fn ensure_nonzero<const N: usize>(value: &[u8; N], field: &str) -> NativeResult<()> {
    if value.iter().all(|byte| *byte == 0) {
        return Err(NativeError::new(
            ErrorCode::InvalidRecord,
            format!("{field} must be nonzero"),
        ));
    }
    Ok(())
}

/// Parse the canonical lowercase GUID-D representation used by C# `Guid`.
/// The bytes are retained in textual order because the value is an opaque
/// application identifier; native code never derives a replacement id.
pub fn parse_record_id(value: &str) -> NativeResult<[u8; RECORD_ID_BYTES]> {
    if value.len() != 36
        || ![8usize, 13, 18, 23]
            .into_iter()
            .all(|index| value.as_bytes().get(index) == Some(&b'-'))
        || value
            .bytes()
            .enumerate()
            .any(|(index, byte)| !matches!(index, 8 | 13 | 18 | 23) && !byte.is_ascii_hexdigit())
    {
        return Err(NativeError::new(
            ErrorCode::InvalidRecord,
            "record id must be canonical lowercase GUID-D",
        ));
    }
    if value.bytes().any(|byte| byte.is_ascii_uppercase()) {
        return Err(NativeError::new(
            ErrorCode::InvalidRecord,
            "record id must use lowercase hexadecimal characters",
        ));
    }
    let compact = value.replace('-', "");
    let id = decode_lower_hex::<RECORD_ID_BYTES>(&compact, "record id")?;
    ensure_nonzero(&id, "record id")?;
    Ok(id)
}

pub fn format_record_id(id: &[u8; RECORD_ID_BYTES]) -> String {
    let encoded = hex::encode(id);
    format!(
        "{}-{}-{}-{}-{}",
        &encoded[0..8],
        &encoded[8..12],
        &encoded[12..16],
        &encoded[16..20],
        &encoded[20..32]
    )
}

pub fn validate_record_id_bytes(id: &[u8; RECORD_ID_BYTES]) -> NativeResult<()> {
    ensure_nonzero(id, "record id")
}

fn payload_hash(payload: &[u8]) -> String {
    let digest = Sha256::digest(payload);
    hex::encode(digest)
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
struct UnsignedEnvelope {
    protocol_version: u16,
    key: Vec<u8>,
    record_id: [u8; RECORD_ID_BYTES],
    actual_author: [u8; ACTUAL_AUTHOR_ID_BYTES],
    generation: Option<u64>,
    revision: u64,
    record_type: String,
    hlc: HlcWire,
    payload: Vec<u8>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct JsonHlc {
    physical_unix_ms: i64,
    logical: u64,
    node_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct JsonEnvelope {
    protocol_version: u16,
    record_id: String,
    actual_author_id: String,
    generation: Option<u64>,
    revision: u64,
    hlc: JsonHlc,
    record_type: String,
    payload: String,
    payload_hash: String,
    signature: String,
    document_key_owner_id: String,
    document_key: String,
    signature_algorithm: String,
}

/// Caller-supplied application metadata and opaque payload for a new envelope.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MeshEnvelopeInput {
    pub key: Vec<u8>,
    pub record_id: [u8; RECORD_ID_BYTES],
    pub generation: Option<u64>,
    pub revision: u64,
    pub record_type: String,
    pub payload: Vec<u8>,
}

/// Rust-authored signed metadata plus opaque C# payload.
///
/// The internal representation signs deterministic postcard bytes. `encode`
/// and `decode` expose the strict canonical JSON v1 interoperability form;
/// the JSON bytes are what Iroh Docs stores, emits, and relays.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MeshEnvelope {
    pub protocol_version: u16,
    pub key: Vec<u8>,
    pub record_id: [u8; RECORD_ID_BYTES],
    pub actual_author: [u8; ACTUAL_AUTHOR_ID_BYTES],
    pub generation: Option<u64>,
    pub revision: u64,
    pub record_type: String,
    pub hlc: HlcWire,
    pub payload: Vec<u8>,
    pub signature: Vec<u8>,
}

impl MeshEnvelope {
    pub fn author_id(&self) -> NativeResult<AuthorId> {
        Ok(AuthorId::from(self.actual_author))
    }

    fn unsigned(&self) -> UnsignedEnvelope {
        UnsignedEnvelope {
            protocol_version: self.protocol_version,
            key: self.key.clone(),
            record_id: self.record_id,
            actual_author: self.actual_author,
            generation: self.generation,
            revision: self.revision,
            record_type: self.record_type.clone(),
            hlc: self.hlc,
            payload: self.payload.clone(),
        }
    }

    fn signing_bytes(&self) -> NativeResult<Vec<u8>> {
        postcard::to_stdvec(&self.unsigned()).map_err(NativeError::from)
    }

    pub fn new(author: &Author, input: MeshEnvelopeInput, hlc: &HLC) -> NativeResult<Self> {
        let timestamp = hlc.new_timestamp();
        Self::new_at_timestamp(author, input, &timestamp)
    }

    pub fn new_at_timestamp(
        author: &Author,
        input: MeshEnvelopeInput,
        timestamp: &Timestamp,
    ) -> NativeResult<Self> {
        let MeshEnvelopeInput {
            key,
            record_id,
            record_type,
            generation,
            revision,
            payload,
        } = input;
        if revision == 0 {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "author revision must be nonzero",
            ));
        }
        if record_type.is_empty() || record_type.len() > MAX_RECORD_TYPE_BYTES {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "record type is empty or exceeds native limit",
            ));
        }
        ensure_nonzero(&record_id, "record id")?;
        let actual_author = *author.id().as_bytes();
        let mut envelope = Self {
            protocol_version: PROTOCOL_VERSION,
            key,
            record_id,
            actual_author,
            generation,
            revision,
            record_type,
            hlc: HlcWire::from_timestamp(timestamp),
            payload,
            signature: Vec::new(),
        };
        let bytes = envelope.signing_bytes()?;
        envelope.signature = author.sign(&bytes).to_bytes().to_vec();
        envelope.validate_shape(usize::MAX)?;
        Ok(envelope)
    }

    fn to_json_value(&self) -> NativeResult<JsonEnvelope> {
        let document_key = String::from_utf8(self.key.clone()).map_err(|_| {
            NativeError::new(
                ErrorCode::InvalidRecord,
                "document key must be valid UTF-8 for JSON interoperability",
            )
        })?;
        Ok(JsonEnvelope {
            protocol_version: self.protocol_version,
            record_id: format_record_id(&self.record_id),
            actual_author_id: hex::encode(self.actual_author),
            generation: self.generation,
            revision: self.revision,
            hlc: JsonHlc {
                physical_unix_ms: self.hlc.physical_unix_ms,
                logical: self.hlc.logical,
                node_id: hex::encode(self.hlc.node_id),
            },
            record_type: self.record_type.clone(),
            payload: BASE64.encode(&self.payload),
            payload_hash: payload_hash(&self.payload),
            signature: BASE64.encode(&self.signature),
            document_key_owner_id: hex::encode(self.actual_author),
            document_key,
            signature_algorithm: SIGNATURE_ALGORITHM.to_owned(),
        })
    }

    fn from_json_value(json: JsonEnvelope, max_value_bytes: usize) -> NativeResult<Self> {
        if json.protocol_version != PROTOCOL_VERSION {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "unsupported mesh protocol version",
            ));
        }
        let record_id = parse_record_id(&json.record_id)?;
        let actual_author =
            decode_lower_hex::<ACTUAL_AUTHOR_ID_BYTES>(&json.actual_author_id, "actualAuthorId")?;
        let node_id = decode_lower_hex::<MAX_NODE_ID_BYTES>(&json.hlc.node_id, "hlc.nodeId")?;
        let payload = BASE64.decode(json.payload.as_bytes()).map_err(|_| {
            NativeError::new(ErrorCode::InvalidRecord, "payload is not valid base64")
        })?;
        if payload.len() > max_value_bytes {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "opaque payload exceeds configured value limit",
            ));
        }
        let expected_payload_hash = payload_hash(&payload);
        if json.payload_hash != expected_payload_hash
            || json
                .payload_hash
                .bytes()
                .any(|byte| byte.is_ascii_uppercase())
        {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "payloadHash does not match the opaque payload",
            ));
        }
        let signature = BASE64.decode(json.signature.as_bytes()).map_err(|_| {
            NativeError::new(ErrorCode::InvalidRecord, "signature is not valid base64")
        })?;
        if signature.len() != SIGNATURE_BYTES {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "invalid envelope signature length",
            ));
        }
        if json.signature_algorithm != SIGNATURE_ALGORITHM {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "unsupported envelope signature algorithm",
            ));
        }
        if json.document_key_owner_id != json.actual_author_id {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "documentKeyOwnerId does not match actualAuthorId",
            ));
        }
        if json.document_key.len() > MAX_KEY_BYTES {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "document key exceeds native limit",
            ));
        }
        let envelope = Self {
            protocol_version: json.protocol_version,
            key: json.document_key.into_bytes(),
            record_id,
            actual_author,
            generation: json.generation,
            revision: json.revision,
            record_type: json.record_type,
            hlc: HlcWire {
                physical_unix_ms: json.hlc.physical_unix_ms,
                logical: json.hlc.logical,
                node_id,
                raw: json.hlc.logical,
            },
            payload,
            signature,
        };
        envelope.validate_shape(max_value_bytes)?;
        Ok(envelope)
    }

    pub fn encode(&self) -> NativeResult<Vec<u8>> {
        self.validate_shape(usize::MAX)?;
        serde_json::to_vec(&self.to_json_value()?)
            .map_err(|error| NativeError::new(ErrorCode::InvalidRecord, error.to_string()))
    }

    pub fn decode(bytes: &[u8], max_value_bytes: usize) -> NativeResult<Self> {
        if bytes.len() > max_value_bytes {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "envelope exceeds configured value limit",
            ));
        }
        let json: JsonEnvelope = serde_json::from_slice(bytes).map_err(|error| {
            NativeError::new(
                ErrorCode::InvalidRecord,
                format!("invalid mesh JSON: {error}"),
            )
        })?;
        let envelope = Self::from_json_value(json, max_value_bytes)?;
        let canonical = envelope.encode()?;
        if canonical != bytes {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "envelope JSON is not canonical v1 encoding",
            ));
        }
        Ok(envelope)
    }

    pub fn validate_shape(&self, max_value_bytes: usize) -> NativeResult<()> {
        if self.protocol_version != PROTOCOL_VERSION {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "unsupported mesh protocol version",
            ));
        }
        if self.key.is_empty() || self.key.len() > MAX_KEY_BYTES {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "invalid envelope key length",
            ));
        }
        std::str::from_utf8(&self.key).map_err(|_| {
            NativeError::new(ErrorCode::InvalidRecord, "document key must be valid UTF-8")
        })?;
        ensure_nonzero(&self.record_id, "record id")?;
        if self.revision == 0 {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "author revision must be nonzero",
            ));
        }
        if self.record_type.is_empty() || self.record_type.len() > MAX_RECORD_TYPE_BYTES {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "record type is empty or exceeds native limit",
            ));
        }
        if self.payload.len() > max_value_bytes {
            return Err(NativeError::new(
                ErrorCode::LimitExceeded,
                "opaque payload exceeds configured value limit",
            ));
        }
        if self.signature.len() != SIGNATURE_BYTES {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "invalid envelope signature length",
            ));
        }
        let timestamp = self.hlc.to_timestamp()?;
        if HlcWire::from_timestamp(&timestamp) != self.hlc {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "HLC metadata does not match its canonical uhlc timestamp",
            ));
        }
        Ok(())
    }

    pub fn validate_record_id_for_key(&self) -> NativeResult<()> {
        validate_record_id_for_key(&self.key, &self.record_type, &self.record_id)
    }
}

pub fn validate_record_id_for_key(
    key_bytes: &[u8],
    record_type: &str,
    record_id: &[u8; RECORD_ID_BYTES],
) -> NativeResult<()> {
    let key = std::str::from_utf8(key_bytes)
        .map_err(|_| NativeError::new(ErrorCode::InvalidRecord, "document key is not UTF-8"))?;
    let requires_guid = matches!(
        record_type,
        "published-list" | "capability-request" | "capability-response" | "inventory-transfer"
    );
    if requires_guid {
        let key_id = key.rsplit('/').next().unwrap_or_default();
        let key_id = parse_record_id(key_id)?;
        if &key_id != record_id {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "record id does not match the GUID in documentKey",
            ));
        }
    }
    Ok(())
}

impl MeshEnvelope {
    pub fn verify(&self, docs_author: AuthorId, expected_key: &[u8]) -> NativeResult<()> {
        self.validate_shape(usize::MAX)?;
        if self.key != expected_key || self.actual_author != *docs_author.as_bytes() {
            return Err(NativeError::new(
                ErrorCode::InvalidRecord,
                "envelope key or actual author does not match the authenticated docs entry",
            ));
        }
        let author = AuthorPublicKey::from_bytes(&self.actual_author)
            .map_err(|error| NativeError::new(ErrorCode::InvalidRecord, error.to_string()))?;
        let signature = <[u8; SIGNATURE_BYTES]>::try_from(self.signature.as_slice())
            .map_err(|_| NativeError::new(ErrorCode::InvalidRecord, "invalid signature bytes"))?;
        let signature = Signature::from_bytes(&signature);
        let bytes = self.signing_bytes()?;
        author
            .verify(&bytes, &signature)
            .map_err(|error| NativeError::new(ErrorCode::InvalidRecord, error.to_string()))
    }

    /// Full canonical JSON envelope BLAKE3, used by Iroh Docs and event
    /// deduplication. This is intentionally distinct from JSON `payloadHash`.
    pub fn content_hash(&self) -> NativeResult<[u8; 32]> {
        let bytes = self.encode()?;
        Ok(*blake3::hash(&bytes).as_bytes())
    }

    pub fn to_timestamp(&self) -> NativeResult<Timestamp> {
        self.hlc.to_timestamp()
    }
}

/// Convert a persisted endpoint secret to an Iroh secret key without exposing it in logs.
pub fn secret_key_from_bytes(bytes: &[u8; 32]) -> SecretKey {
    SecretKey::from_bytes(bytes)
}
