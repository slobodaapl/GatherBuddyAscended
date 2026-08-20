#![deny(unsafe_op_in_unsafe_fn)]
#![allow(clippy::missing_safety_doc)]

mod document;
mod error;
mod events;
mod ffi;
mod node;
mod persistence;
mod runtime;

pub use document::{
    ACTUAL_AUTHOR_ID_BYTES, HlcWire, MeshEnvelope, MeshEnvelopeInput, PROTOCOL_VERSION,
    RECORD_ID_BYTES, SIGNATURE_ALGORITHM, format_record_id, parse_record_id,
    validate_record_id_bytes, validate_record_id_for_key,
};
pub use error::{ErrorCode, NativeError};
pub use events::{EventKind, NativeEvent};
pub use ffi::*;
