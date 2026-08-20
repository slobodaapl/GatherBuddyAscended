use std::time::Duration;

use gathermesh_ffi::{HlcWire, MeshEnvelope, MeshEnvelopeInput, PROTOCOL_VERSION, parse_record_id};
use iroh_docs::Author;
use uhlc::{HLCBuilder, ID, NTP64, Timestamp};

#[test]
fn accepted_remote_hlc_precedes_later_local_stamp() {
    let remote = HLCBuilder::new()
        .with_max_delta(Duration::from_secs(300))
        .build();
    let local = HLCBuilder::new()
        .with_max_delta(Duration::from_secs(300))
        .build();
    let remote_timestamp = remote.new_timestamp();
    local
        .update_with_timestamp(&remote_timestamp)
        .expect("remote timestamp should be sane");
    assert!(local.new_timestamp() > remote_timestamp);
}

#[test]
fn concurrent_hlc_wire_values_have_a_deterministic_total_order() {
    let first = HLCBuilder::new().build().new_timestamp();
    let second = HLCBuilder::new().build().new_timestamp();
    let a = HlcWire::from_timestamp(&first);
    let b = HlcWire::from_timestamp(&second);
    assert_ne!(a.ordering_key(), b.ordering_key());
    assert_eq!(a.cmp(&b).is_eq(), b.cmp(&a).is_eq());
    assert_eq!(a.cmp(&b), b.cmp(&a).reverse());
}

#[test]
fn relaying_an_envelope_preserves_author_hlc_and_payload() {
    let author = Author::from_bytes(&[7u8; 32]);
    let hlc = HLCBuilder::new()
        .with_max_delta(Duration::from_secs(300))
        .build();
    let key = format!("v1/workers/{}", hex::encode(author.id().as_bytes())).into_bytes();
    let envelope = MeshEnvelope::new(
        &author,
        MeshEnvelopeInput {
            key,
            record_id: [1u8; 16],
            record_type: "worker-session".to_owned(),
            generation: Some(4),
            revision: 12,
            payload: b"opaque".to_vec(),
        },
        &hlc,
    )
    .expect("envelope should sign");
    let encoded = envelope.encode().expect("envelope should encode");
    assert!(encoded.starts_with(b"{\"protocolVersion\":1,"));
    let json: serde_json::Value =
        serde_json::from_slice(&encoded).expect("wire envelope should be JSON");
    assert_eq!(json["recordId"], "01010101-0101-0101-0101-010101010101");
    assert_eq!(
        json["payloadHash"],
        "6d229884c1268bb0ab32d8da315d0fe52f9147228bd830a37bc9fb28a954940d"
    );
    let relayed = MeshEnvelope::decode(&encoded, 1024).expect("relayed envelope should decode");
    assert_eq!(
        relayed.encode().expect("decoded envelope should re-encode"),
        encoded,
        "canonical JSON relay bytes must be stable"
    );
    assert_eq!(relayed.actual_author, envelope.actual_author);
    assert_eq!(relayed.hlc, envelope.hlc);
    assert_eq!(relayed.payload, b"opaque");
    assert_eq!(relayed.revision, 12);
    assert_eq!(relayed.protocol_version, PROTOCOL_VERSION);
    relayed
        .verify(author.id(), &relayed.key)
        .expect("original author signature must remain valid");

    let mut tampered = json;
    tampered["payloadHash"] = serde_json::Value::String("00".repeat(32));
    let tampered = serde_json::to_vec(&tampered).expect("tampered JSON should serialize");
    assert!(MeshEnvelope::decode(&tampered, 1024).is_err());

    let mut unknown =
        serde_json::from_slice::<serde_json::Value>(&encoded).expect("canonical JSON should parse");
    unknown["unexpected"] = serde_json::Value::Bool(true);
    let unknown = serde_json::to_vec(&unknown).expect("unknown-field JSON should serialize");
    assert!(MeshEnvelope::decode(&unknown, 1024).is_err());
}

#[test]
fn record_id_is_explicit_canonical_guid_and_not_derived_from_payload() {
    let id = parse_record_id("00112233-4455-6677-8899-aabbccddeeff")
        .expect("canonical GUID should parse");
    assert_eq!(
        gathermesh_ffi::format_record_id(&id),
        "00112233-4455-6677-8899-aabbccddeeff"
    );
    assert!(parse_record_id("00112233-4455-6677-8899-AABBCCDDEEFF").is_err());
    assert!(parse_record_id("00000000-0000-0000-0000-000000000000").is_err());
}

#[test]
fn golden_json_fixture_is_verified_and_byte_stable() {
    let bytes: &[u8] = include_bytes!("fixtures/mesh-envelope-v1.json");
    let json: serde_json::Value =
        serde_json::from_slice(bytes).expect("golden fixture should be valid JSON");
    assert_eq!(
        json["actualAuthorId"],
        "ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c"
    );
    assert_eq!(json["payload"], "b3BhcXVlLWZpeHR1cmU=");
    assert_eq!(
        json["payloadHash"],
        "b15740944697339d5caf7fca29223315a816ceceaa9f22f10b068284236054aa"
    );
    assert_eq!(
        json["signature"],
        "xsBV2Zpt6/6DMr7q1pRLVowUDXSW5DPrVrfLdtFZCY6VnXqHFHubniR64QT4idreT//IOgeYqOb0o/8jCntXBg=="
    );
    assert_eq!(json["hlc"]["nodeId"], "22222222222222222222222222222222");
    let envelope = MeshEnvelope::decode(bytes, 4096).expect("golden JSON fixture should decode");
    let author = Author::from_bytes(&[7u8; 32]);
    envelope
        .verify(author.id(), &envelope.key)
        .expect("golden fixture signature should verify");
    assert_eq!(
        envelope.record_id,
        parse_record_id("00112233-4455-6677-8899-aabbccddeeff").expect("fixture id")
    );
    assert_eq!(envelope.actual_author, *author.id().as_bytes());
    assert_eq!(envelope.revision, 7);
    assert_eq!(envelope.record_type, "worker-session");
    assert_eq!(envelope.payload, b"opaque-fixture");
    assert_eq!(envelope.hlc.physical_unix_ms, 1_700_000_000_000);
    assert_eq!(envelope.hlc.logical, 7_301_444_403_200_000_000);
    assert_eq!(
        envelope.encode().expect("fixture should re-encode"),
        bytes,
        "fixture must be canonical compact JSON"
    );
}

#[test]
fn malformed_or_oversized_envelope_is_rejected_before_use() {
    assert!(MeshEnvelope::decode(&[1, 2, 3], 2).is_err());
    let author = Author::from_bytes(&[8u8; 32]);
    let hlc = HLCBuilder::new().build();
    let key = format!("v1/workers/{}", hex::encode(author.id().as_bytes())).into_bytes();
    let envelope = MeshEnvelope::new(
        &author,
        MeshEnvelopeInput {
            key,
            record_id: [2u8; 16],
            record_type: "worker-session".to_owned(),
            generation: None,
            revision: 1,
            payload: vec![0u8; 32],
        },
        &hlc,
    )
    .expect("envelope should sign");
    let encoded = envelope.encode().expect("envelope should encode");
    assert!(MeshEnvelope::decode(&encoded, 16).is_err());
}

#[test]
fn excessive_remote_hlc_delta_is_rejected_by_sane_clock_policy() {
    let local = HLCBuilder::new()
        .with_max_delta(Duration::from_secs(300))
        .build();
    let node_id = ID::try_from(&[1u8; 16]).expect("test HLC node id should be valid");
    let remote = Timestamp::new(NTP64(u64::MAX), node_id);
    assert!(local.update_with_timestamp(&remote).is_err());
}
