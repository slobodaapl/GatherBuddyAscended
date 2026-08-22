use std::panic::{AssertUnwindSafe, catch_unwind};

use gathermesh_ffi::{HlcWire, MeshEnvelope, PROTOCOL_VERSION};

const MAX_VALUE_BYTES: usize = 4096;

#[test]
fn canonical_decoder_survives_deterministic_arbitrary_byte_mutations() {
    let fixture = include_bytes!("fixtures/mesh-envelope-v1.json");
    let mut accepted = 0usize;
    for seed in 1..=2048u32 {
        let mutated = mutate(fixture, seed);
        let outcome = catch_unwind(AssertUnwindSafe(|| {
            let decoded = MeshEnvelope::decode(&mutated, MAX_VALUE_BYTES);
            if let Ok(envelope) = &decoded {
                assert_eq!(
                    envelope.encode().expect("accepted envelope must encode"),
                    mutated,
                    "accepted bytes must already be canonical"
                );
                accepted += 1;
            }
            decoded.is_ok()
        }));
        assert!(outcome.is_ok(), "decoder panicked for mutation seed {seed}");
    }
    assert!(
        accepted < 2048,
        "mutations must not bypass canonical validation"
    );
}

#[test]
fn structured_protocol_and_optional_field_mutations_fail_closed() {
    let fixture = include_bytes!("fixtures/mesh-envelope-v1.json");
    let value: serde_json::Value = serde_json::from_slice(fixture).expect("fixture JSON");

    let mut unknown_envelope_field = value.clone();
    unknown_envelope_field["optionalExtension"] = serde_json::Value::Bool(true);
    let unknown_envelope_field =
        serde_json::to_vec(&unknown_envelope_field).expect("unknown-field JSON");
    assert!(MeshEnvelope::decode(&unknown_envelope_field, MAX_VALUE_BYTES).is_err());

    let mut major = value.clone();
    major["protocolVersion"] = serde_json::Value::from(PROTOCOL_VERSION + 1);
    let major = serde_json::to_vec(&major).expect("new-major JSON");
    assert!(MeshEnvelope::decode(&major, MAX_VALUE_BYTES).is_err());

    let oversized = vec![b'x'; MAX_VALUE_BYTES + 1];
    assert!(MeshEnvelope::decode(&oversized, MAX_VALUE_BYTES).is_err());
}

#[test]
fn hlc_wire_order_is_total_and_transitive_for_independent_nodes() {
    let values = (0u8..32)
        .map(|index| HlcWire {
            physical_unix_ms: i64::from(index % 5),
            logical: 100 + u64::from(index / 5),
            node_id: [index; 16],
            raw: 100 + u64::from(index / 5),
        })
        .collect::<Vec<_>>();
    for first in &values {
        for second in &values {
            assert_eq!(
                first.cmp(second),
                second.cmp(first).reverse(),
                "HLC comparison must be antisymmetric"
            );
            for third in &values {
                if first <= second && second <= third {
                    assert!(first <= third, "HLC comparison must be transitive");
                }
            }
        }
    }
}

fn mutate(source: &[u8], seed: u32) -> Vec<u8> {
    let mut result = source.to_vec();
    let mut state = seed.wrapping_mul(0x9E37_79B9);
    let edits = 1 + (seed as usize % 7);
    for _ in 0..edits {
        state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
        let index = (state as usize) % result.len();
        state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
        result[index] ^= 1u8 << (state % 8);
    }
    if seed & 1 == 0 {
        result.push(b' ');
    }
    if seed & 3 == 0 {
        result.pop();
    }
    result
}
