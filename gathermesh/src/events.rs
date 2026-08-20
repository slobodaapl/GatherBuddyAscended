use std::{
    collections::{BTreeMap, VecDeque},
    time::Instant,
};

use crate::document::{HlcWire, MeshEnvelope, PROTOCOL_VERSION};

/// Stable native event kinds. Values are part of ABI version 2.
#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EventKind {
    Started = 1,
    Stopped = 2,
    Joining = 3,
    Joined = 4,
    Left = 5,
    RecordInserted = 6,
    RecordRemoved = 7,
    InitialSyncCompleted = 8,
    PeerConnected = 9,
    PeerDisconnected = 10,
    PathChanged = 11,
    Warning = 12,
    Error = 13,
    WorldInvalidated = 14,
    SnapshotReady = 15,
}

impl EventKind {
    pub const fn is_record(self) -> bool {
        matches!(self, Self::RecordInserted | Self::RecordRemoved)
    }
}

#[derive(Debug, Clone)]
pub struct NativeEvent {
    pub protocol_version: u16,
    pub kind: EventKind,
    pub sequence: u64,
    pub world_epoch: u64,
    pub key: Vec<u8>,
    pub value: Vec<u8>,
    pub actual_author: Vec<u8>,
    pub content_hash: Vec<u8>,
    pub aux: u64,
}

impl NativeEvent {
    pub fn simple(kind: EventKind) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION,
            kind,
            sequence: 0,
            world_epoch: 0,
            key: Vec::new(),
            value: Vec::new(),
            actual_author: Vec::new(),
            content_hash: Vec::new(),
            aux: 0,
        }
    }

    pub fn record(envelope: &MeshEnvelope, kind: EventKind) -> Self {
        let value = envelope.encode().unwrap_or_default();
        let content_hash = envelope.content_hash().unwrap_or_default().to_vec();
        Self {
            protocol_version: envelope.protocol_version,
            kind,
            sequence: 0,
            world_epoch: 0,
            key: envelope.key.clone(),
            value,
            actual_author: envelope.actual_author.to_vec(),
            content_hash,
            aux: envelope.revision,
        }
    }
}

/// Bounded queue with per-key record coalescing and non-droppable invalidation.
#[derive(Debug)]
pub struct EventQueue {
    capacity: usize,
    next_sequence: u64,
    world_epoch: u64,
    events: VecDeque<NativeEvent>,
    record_positions: BTreeMap<Vec<u8>, u64>,
    invalidation_sequence: Option<u64>,
}

impl EventQueue {
    pub fn new(capacity: usize) -> Self {
        Self {
            // Keep one slot for the non-droppable invalidation even when callers request the
            // smallest queue; correctness requires an invalidation and the triggering record
            // to be observable independently.
            capacity: capacity.max(2),
            next_sequence: 0,
            world_epoch: 0,
            events: VecDeque::new(),
            record_positions: BTreeMap::new(),
            invalidation_sequence: None,
        }
    }

    pub fn epoch(&self) -> u64 {
        self.world_epoch
    }

    pub fn latest_sequence(&self) -> u64 {
        self.next_sequence
    }

    pub fn len(&self) -> usize {
        self.events.len()
    }

    pub fn push(&mut self, mut event: NativeEvent) {
        self.next_sequence = self.next_sequence.saturating_add(1);
        event.sequence = self.next_sequence;
        event.world_epoch = self.world_epoch;

        if event.kind.is_record()
            && let Some(previous_sequence) = self.record_positions.get(&event.key).copied()
        {
            if let Some(previous) = self
                .events
                .iter_mut()
                .find(|candidate| candidate.sequence == previous_sequence)
            {
                event.world_epoch = self.world_epoch;
                *previous = event.clone();
                self.record_positions.insert(event.key, event.sequence);
                return;
            }
            self.record_positions.remove(&event.key);
        }

        if self.events.len() >= self.capacity {
            self.world_epoch = self.world_epoch.saturating_add(1);
            self.record_positions.clear();
            self.enqueue_invalidation();
            if event.kind == EventKind::WorldInvalidated {
                return;
            }
            if self.events.len() >= self.capacity {
                self.drop_oldest_non_invalidation();
            }
            event.world_epoch = self.world_epoch;
        }

        if event.kind == EventKind::WorldInvalidated {
            self.invalidation_sequence = Some(event.sequence);
        } else if event.kind.is_record() {
            self.record_positions
                .insert(event.key.clone(), event.sequence);
        }
        self.events.push_back(event);
    }

    fn enqueue_invalidation(&mut self) {
        if let Some(sequence) = self.invalidation_sequence {
            if let Some(event) = self
                .events
                .iter_mut()
                .find(|event| event.sequence == sequence)
            {
                event.world_epoch = self.world_epoch;
                event.aux = self.world_epoch;
                return;
            }
            self.invalidation_sequence = None;
        }
        if self.events.len() >= self.capacity {
            self.drop_oldest_non_invalidation();
        }
        self.next_sequence = self.next_sequence.saturating_add(1);
        let event = NativeEvent {
            protocol_version: PROTOCOL_VERSION,
            kind: EventKind::WorldInvalidated,
            sequence: self.next_sequence,
            world_epoch: self.world_epoch,
            key: Vec::new(),
            value: Vec::new(),
            actual_author: Vec::new(),
            content_hash: Vec::new(),
            aux: self.world_epoch,
        };
        self.invalidation_sequence = Some(event.sequence);
        self.events.push_back(event);
    }

    fn drop_oldest_non_invalidation(&mut self) {
        let Some(index) = self
            .events
            .iter()
            .position(|event| Some(event.sequence) != self.invalidation_sequence)
        else {
            return;
        };
        self.events.remove(index);
    }

    pub fn pop(&mut self) -> Option<NativeEvent> {
        let event = self.events.pop_front()?;
        if event.kind.is_record() {
            self.record_positions.remove(&event.key);
        }
        if event.kind == EventKind::WorldInvalidated {
            self.invalidation_sequence = None;
        }
        Some(event)
    }
}

#[derive(Debug, Clone)]
pub struct SnapshotRecord {
    pub protocol_version: u16,
    pub key: Vec<u8>,
    pub value: Vec<u8>,
    pub actual_author: Vec<u8>,
    pub content_hash: Vec<u8>,
    pub generation: Option<u64>,
    pub revision: u64,
    pub record_type: String,
    pub hlc: HlcWire,
}

#[derive(Debug)]
pub struct PreparedSnapshot {
    pub epoch: u64,
    pub base_event_sequence: u64,
    pub records: Vec<SnapshotRecord>,
    pub created_at: Instant,
}

#[cfg(test)]
mod tests {
    use super::*;
    use iroh_docs::Author;
    use sha2::{Digest, Sha256};
    use uhlc::HLCBuilder;

    #[test]
    fn record_overflow_keeps_world_invalidation() {
        let mut queue = EventQueue::new(2);
        for key in [b"a".to_vec(), b"b".to_vec(), b"c".to_vec()] {
            let mut event = NativeEvent::simple(EventKind::RecordInserted);
            event.key = key;
            queue.push(event);
        }
        assert!(queue.epoch() > 0);
        assert!(
            queue
                .events
                .iter()
                .any(|event| event.kind == EventKind::WorldInvalidated)
        );
    }

    #[test]
    fn repeated_overflow_keeps_latest_invalidation_non_droppable() {
        let mut queue = EventQueue::new(2);
        for index in 0..16u8 {
            let mut event = NativeEvent::simple(EventKind::RecordInserted);
            event.key = vec![index];
            queue.push(event);
        }
        let invalidation = queue
            .events
            .iter()
            .find(|event| event.kind == EventKind::WorldInvalidated)
            .expect("repeated overflow must retain invalidation");
        assert_eq!(invalidation.world_epoch, queue.epoch());
        assert_eq!(invalidation.aux, queue.epoch());
    }

    #[test]
    fn repeated_key_updates_coalesce_without_dropping_latest_value() {
        let mut queue = EventQueue::new(4);
        let mut first = NativeEvent::simple(EventKind::RecordInserted);
        first.key = b"same".to_vec();
        first.value = b"old".to_vec();
        queue.push(first);
        let mut second = NativeEvent::simple(EventKind::RecordInserted);
        second.key = b"same".to_vec();
        second.value = b"new".to_vec();
        queue.push(second);
        let event = queue.pop().expect("coalesced event should remain");
        assert_eq!(event.value, b"new");
        assert!(queue.pop().is_none());
    }

    #[test]
    fn record_event_hash_is_full_envelope_blake3_not_payload_sha256() {
        let author = Author::from_bytes(&[0x44; 32]);
        let hlc = HLCBuilder::new().build();
        let key = format!("v1/workers/{}", hex::encode(author.id().as_bytes())).into_bytes();
        let envelope = MeshEnvelope::new(
            &author,
            crate::document::MeshEnvelopeInput {
                key,
                record_id: [0x77; 16],
                record_type: "worker-session".to_owned(),
                generation: None,
                revision: 1,
                payload: b"event-payload".to_vec(),
            },
            &hlc,
        )
        .expect("event envelope should sign");
        let encoded = envelope.encode().expect("event envelope should encode");
        let event = NativeEvent::record(&envelope, EventKind::RecordInserted);
        let expected_content_hash = *blake3::hash(&encoded).as_bytes();
        let payload_hash = Sha256::digest(b"event-payload");

        assert_eq!(event.protocol_version, PROTOCOL_VERSION);
        assert_eq!(event.value, encoded);
        assert_eq!(event.content_hash, expected_content_hash);
        assert_eq!(event.content_hash.len(), 32);
        assert_ne!(event.content_hash, payload_hash.as_slice());
    }
}
