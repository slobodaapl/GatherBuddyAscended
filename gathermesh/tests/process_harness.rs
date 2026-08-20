use std::{
    env, fs,
    process::{Command, Stdio},
    thread,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use gathermesh_ffi::MeshEnvelope;

#[test]
fn separate_process_create_join_restart_smoke() {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_nanos();
    let root = env::temp_dir().join(format!("gathermesh-process-{stamp}"));
    let ticket = root.join("ticket.txt");
    let host_store = root.join("host");
    let join_store = root.join("join");
    let host_observed = root.join("host-observed");
    let join_observed = root.join("join-observed");
    let host_published = root.join("host-published");
    let join_published = root.join("join-published");
    fs::create_dir_all(&root).expect("test root should be creatable");
    let binary = env!("CARGO_BIN_EXE_gathermesh-node");

    let mut host = Command::new(binary)
        .args([
            "--mode",
            "create",
            "--character",
            "process-host",
            "--storage",
            host_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            "host-record",
            "--expect-payload",
            "join-record",
            "--observed-file",
            host_observed.to_str().expect("UTF-8 observation path"),
            "--published-file",
            host_published.to_str().expect("UTF-8 published path"),
            "--seconds",
            "12",
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("host process should spawn");
    for _ in 0..80 {
        if ticket.exists() {
            break;
        }
        thread::sleep(Duration::from_millis(100));
    }
    assert!(
        ticket.exists(),
        "creator must persist a complete invitation ticket"
    );

    let join_status = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "process-join",
            "--storage",
            join_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            "join-record",
            "--expect-payload",
            "host-record",
            "--observed-file",
            join_observed.to_str().expect("UTF-8 observation path"),
            "--published-file",
            join_published.to_str().expect("UTF-8 published path"),
            "--seconds",
            "5",
        ])
        .status()
        .expect("join process should spawn");
    assert!(
        join_status.success(),
        "join process should complete cleanly"
    );
    let host_status = host.wait().expect("host process should complete");
    assert!(
        host_status.success(),
        "creator process should complete cleanly"
    );
    assert!(
        host_observed.exists(),
        "creator must observe the joiner's opaque record"
    );
    assert!(
        join_observed.exists(),
        "joiner must observe the creator's opaque record"
    );
    assert_observed_envelope(&host_observed, b"join-record");
    assert_observed_envelope(&join_observed, b"host-record");
    assert_relayed_envelope(&join_published, &host_observed, b"join-record");
    assert_relayed_envelope(&host_published, &join_observed, b"host-record");

    let restart = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "process-join",
            "--storage",
            join_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            ticket.to_str().expect("UTF-8 ticket path"),
            "--seconds",
            "2",
        ])
        .status()
        .expect("restart process should spawn");
    assert!(
        restart.success(),
        "restarted process should complete cleanly"
    );
    let _ = fs::remove_dir_all(root);
}

#[test]
fn corrupted_persistent_manifest_fails_without_starting_native_process() {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_nanos();
    let root = env::temp_dir().join(format!("gathermesh-corrupt-{stamp}"));
    fs::create_dir_all(&root).expect("test root should be creatable");
    fs::write(root.join("native-state.json"), b"not-json")
        .expect("corrupt manifest should be writable");
    let binary = env!("CARGO_BIN_EXE_gathermesh-node");
    let status = Command::new(binary)
        .args([
            "--mode",
            "create",
            "--storage",
            root.to_str().expect("UTF-8 temp path"),
            "--seconds",
            "1",
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .expect("corruption process should spawn");
    assert!(
        !status.success(),
        "corrupt persistent state must fail visibly"
    );
    let _ = fs::remove_dir_all(root);
}

#[test]
fn separate_process_line_propagates_opaque_records_through_intermediate_peer() {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_nanos();
    let root = env::temp_dir().join(format!("gathermesh-line-{stamp}"));
    let a_store = root.join("a");
    let c_store = root.join("c");
    let b_store = root.join("b");
    let a_ticket = root.join("a-ticket.txt");
    let c_ticket = root.join("c-ticket.txt");
    let a_observed = root.join("a-observed");
    let b_observed = root.join("b-observed");
    let a_published = root.join("a-published");
    let b_published = root.join("b-published");
    fs::create_dir_all(&root).expect("test root should be creatable");
    let binary = env!("CARGO_BIN_EXE_gathermesh-node");

    let mut a = Command::new(binary)
        .args([
            "--mode",
            "create",
            "--character",
            "line-a",
            "--storage",
            a_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            a_ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            "line-a-record",
            "--expect-payload",
            "line-b-record",
            "--observed-file",
            a_observed.to_str().expect("UTF-8 observation path"),
            "--published-file",
            a_published.to_str().expect("UTF-8 published path"),
            "--seconds",
            "30",
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("A process should spawn");
    wait_for_file(&a_ticket);

    let mut c = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "line-c",
            "--storage",
            c_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            a_ticket.to_str().expect("UTF-8 ticket path"),
            "--export-ticket-file",
            c_ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            "line-c-record",
            "--seconds",
            "25",
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("C process should spawn");
    wait_for_file(&c_ticket);

    let b_status = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "line-b",
            "--storage",
            b_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            c_ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            "line-b-record",
            "--expect-payload",
            "line-a-record",
            "--observed-file",
            b_observed.to_str().expect("UTF-8 observation path"),
            "--published-file",
            b_published.to_str().expect("UTF-8 published path"),
            "--seconds",
            "10",
        ])
        .status()
        .expect("B process should spawn");
    assert!(b_status.success(), "B process should complete cleanly");
    let c_status = c.wait().expect("C process should complete");
    let a_status = a.wait().expect("A process should complete");
    assert!(c_status.success(), "C process should complete cleanly");
    assert!(a_status.success(), "A process should complete cleanly");
    assert!(a_observed.exists(), "A must observe B through C");
    assert!(b_observed.exists(), "B must observe A through C");
    assert_observed_envelope(&a_observed, b"line-b-record");
    assert_observed_envelope(&b_observed, b"line-a-record");
    assert_relayed_envelope(&b_published, &a_observed, b"line-b-record");
    assert_relayed_envelope(&a_published, &b_observed, b"line-a-record");
    let _ = fs::remove_dir_all(root);
}

fn wait_for_file(path: &std::path::Path) {
    for _ in 0..100 {
        if path.exists() {
            return;
        }
        thread::sleep(Duration::from_millis(100));
    }
    panic!(
        "expected process output file was not created: {}",
        path.display()
    );
}

#[test]
fn separate_process_partition_reconnect_reconciles_after_intermediate_restart() {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_nanos();
    let root = env::temp_dir().join(format!("gathermesh-reconnect-{stamp}"));
    let a_store = root.join("a");
    let c_store = root.join("c");
    let b_store = root.join("b");
    let a_ticket = root.join("a-ticket.txt");
    let c_ticket = root.join("c-ticket.txt");
    let a_observed = root.join("a-observed");
    let c_observed = root.join("c-reconnected");
    let b_published = root.join("b-published");
    fs::create_dir_all(&root).expect("test root should be creatable");
    let binary = env!("CARGO_BIN_EXE_gathermesh-node");

    let mut a = Command::new(binary)
        .args([
            "--mode",
            "create",
            "--character",
            "reconnect-a",
            "--storage",
            a_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            a_ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            "reconnect-a-record",
            "--expect-payload",
            "reconnect-b-record",
            "--observed-file",
            a_observed.to_str().expect("UTF-8 observation path"),
            "--seconds",
            "24",
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("A process should spawn");
    wait_for_file(&a_ticket);

    let mut c = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "reconnect-c",
            "--storage",
            c_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            a_ticket.to_str().expect("UTF-8 ticket path"),
            "--export-ticket-file",
            c_ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            "reconnect-c-record",
            "--seconds",
            "24",
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("C process should spawn");
    wait_for_file(&c_ticket);

    let _ = c.kill();
    let _ = c.wait();

    let mut b = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "reconnect-b",
            "--storage",
            b_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            c_ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            "reconnect-b-record",
            "--published-file",
            b_published.to_str().expect("UTF-8 published path"),
            "--seconds",
            "16",
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("B process should spawn");

    let c_restart = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "reconnect-c",
            "--storage",
            c_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            a_ticket.to_str().expect("UTF-8 ticket path"),
            "--expect-payload",
            "reconnect-b-record",
            "--observed-file",
            c_observed.to_str().expect("UTF-8 observation path"),
            "--seconds",
            "10",
        ])
        .status()
        .expect("restarted C process should spawn");
    assert!(
        c_restart.success(),
        "restarted C process should complete cleanly"
    );
    let b_status = b.wait().expect("B process should complete");
    let a_status = a.wait().expect("A process should complete");
    assert!(b_status.success(), "B process should complete cleanly");
    assert!(a_status.success(), "A process should complete cleanly");
    assert!(c_observed.exists(), "restarted C must reconcile B's record");
    assert!(
        a_observed.exists(),
        "A must receive B's record after C restart"
    );
    assert_observed_envelope(&c_observed, b"reconnect-b-record");
    assert_observed_envelope(&a_observed, b"reconnect-b-record");
    assert_relayed_envelope(&b_published, &c_observed, b"reconnect-b-record");
    assert_relayed_envelope(&b_published, &a_observed, b"reconnect-b-record");
    let _ = fs::remove_dir_all(root);
}

#[test]
fn separate_process_large_opaque_payload_reaches_remote_register() {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_nanos();
    let root = env::temp_dir().join(format!("gathermesh-large-{stamp}"));
    let host_store = root.join("host");
    let join_store = root.join("join");
    let ticket = root.join("ticket.txt");
    let observed = root.join("observed");
    let published = root.join("published");
    fs::create_dir_all(&root).expect("test root should be creatable");
    let binary = env!("CARGO_BIN_EXE_gathermesh-node");

    let mut host = Command::new(binary)
        .args([
            "--mode",
            "create",
            "--character",
            "large-host",
            "--storage",
            host_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            ticket.to_str().expect("UTF-8 ticket path"),
            "--seconds",
            "18",
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("host process should spawn");
    wait_for_file(&ticket);
    let join_status = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "large-join",
            "--storage",
            join_store.to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            ticket.to_str().expect("UTF-8 ticket path"),
            "--put-bytes",
            "1048576",
            "--published-file",
            published.to_str().expect("UTF-8 published path"),
            "--seconds",
            "8",
        ])
        .status()
        .expect("join process should spawn");
    assert!(
        join_status.success(),
        "join process should complete cleanly"
    );
    let verify_status = Command::new(binary)
        .args([
            "--mode",
            "join",
            "--character",
            "large-verify",
            "--storage",
            root.join("verify").to_str().expect("UTF-8 temp path"),
            "--ticket-file",
            ticket.to_str().expect("UTF-8 ticket path"),
            "--expect-bytes",
            "1048576",
            "--observed-file",
            observed.to_str().expect("UTF-8 observation path"),
            "--seconds",
            "8",
        ])
        .status()
        .expect("verification process should spawn");
    assert!(
        verify_status.success(),
        "verification process should complete cleanly"
    );
    let host_status = host.wait().expect("host process should complete");
    assert!(
        host_status.success(),
        "host process should complete cleanly"
    );
    assert!(
        observed.exists(),
        "remote must observe the large opaque payload"
    );
    assert_observed_envelope_len(&observed, 1_048_576);
    assert_relayed_envelope_len(&published, &observed, 1_048_576);
    let _ = fs::remove_dir_all(root);
}

fn assert_observed_envelope(path: &std::path::Path, payload: &[u8]) {
    let envelope = read_observed_envelope(path);
    assert_eq!(envelope.payload, payload);
}

fn assert_observed_envelope_len(path: &std::path::Path, payload_len: usize) {
    let envelope = read_observed_envelope(path);
    assert_eq!(envelope.payload.len(), payload_len);
}

fn assert_relayed_envelope(
    source_path: &std::path::Path,
    observed_path: &std::path::Path,
    payload: &[u8],
) {
    let source_bytes = fs::read(source_path).expect("source envelope should be readable");
    let observed_bytes = fs::read(observed_path).expect("observed envelope should be readable");
    assert_eq!(
        observed_bytes, source_bytes,
        "relay must preserve the canonical envelope bytes"
    );
    let source = read_observed_envelope(source_path);
    let observed = read_observed_envelope(observed_path);
    assert_eq!(source.payload, payload);
    assert_eq!(observed.payload, payload);
    assert_eq!(observed.actual_author, source.actual_author);
    assert_eq!(observed.hlc, source.hlc);
    assert_eq!(observed.record_id, source.record_id);
    assert_eq!(observed.key, source.key);
    assert_eq!(observed.record_type, source.record_type);
    assert_eq!(observed.signature, source.signature);
}

fn assert_relayed_envelope_len(
    source_path: &std::path::Path,
    observed_path: &std::path::Path,
    payload_len: usize,
) {
    let source_bytes = fs::read(source_path).expect("source envelope should be readable");
    let observed_bytes = fs::read(observed_path).expect("observed envelope should be readable");
    assert_eq!(
        observed_bytes, source_bytes,
        "relay must preserve the canonical envelope bytes"
    );
    let source = read_observed_envelope(source_path);
    let observed = read_observed_envelope(observed_path);
    assert_eq!(source.payload.len(), payload_len);
    assert_eq!(observed.payload.len(), payload_len);
    assert_eq!(observed.actual_author, source.actual_author);
    assert_eq!(observed.hlc, source.hlc);
    assert_eq!(observed.record_id, source.record_id);
    assert_eq!(observed.key, source.key);
    assert_eq!(observed.record_type, source.record_type);
    assert_eq!(observed.signature, source.signature);
}

fn read_observed_envelope(path: &std::path::Path) -> MeshEnvelope {
    let bytes = fs::read(path).expect("observed envelope should be readable");
    let envelope = MeshEnvelope::decode(&bytes, 16 * 1024 * 1024)
        .expect("observed bytes must be a valid native envelope");
    assert!(
        envelope.actual_author.iter().any(|byte| *byte != 0),
        "observed envelope must retain its original author"
    );
    assert_ne!(
        envelope.hlc.raw, 0,
        "observed envelope must retain HLC metadata"
    );
    envelope
}
