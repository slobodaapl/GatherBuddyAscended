use std::fmt;

/// Stable result codes exposed by the native ABI.
#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorCode {
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    InvalidHandle = 3,
    InvalidState = 4,
    QueueFull = 5,
    NoEvent = 6,
    NotReady = 7,
    Closing = 8,
    AlreadyDestroyed = 9,
    NotFound = 10,
    LimitExceeded = 11,
    InvalidRecord = 12,
    ClockDrift = 13,
    Storage = 14,
    Network = 15,
    Panic = 16,
    Internal = 17,
    SnapshotStale = 18,
}

impl ErrorCode {
    pub const fn as_u32(self) -> u32 {
        self as u32
    }
}

/// Error carried by a command or recorded in the bounded native error table.
#[derive(Debug, Clone)]
pub struct NativeError {
    pub code: ErrorCode,
    pub message: String,
}

impl NativeError {
    pub fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }

    pub fn invalid(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::InvalidArgument, message)
    }

    pub fn state(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::InvalidState, message)
    }
}

impl fmt::Display for NativeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.code.as_u32(), self.message)
    }
}

impl std::error::Error for NativeError {}

impl From<anyhow::Error> for NativeError {
    fn from(error: anyhow::Error) -> Self {
        Self::new(ErrorCode::Internal, error.to_string())
    }
}

impl From<std::io::Error> for NativeError {
    fn from(error: std::io::Error) -> Self {
        Self::new(ErrorCode::Storage, error.to_string())
    }
}

impl From<postcard::Error> for NativeError {
    fn from(error: postcard::Error) -> Self {
        Self::new(ErrorCode::InvalidRecord, error.to_string())
    }
}

pub type NativeResult<T> = Result<T, NativeError>;
