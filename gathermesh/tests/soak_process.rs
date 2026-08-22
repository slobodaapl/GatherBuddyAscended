use std::{
    env, fs,
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

struct Schedule {
    state: u64,
}

struct PeerLaunch<'a> {
    seed: u64,
    round: u64,
    seconds: &'a str,
    host_payload: &'a str,
}

impl Schedule {
    fn new(seed: u64) -> Self {
        Self { state: seed.max(1) }
    }

    fn next(&mut self) -> u64 {
        self.state = self
            .state
            .wrapping_mul(6_364_136_223_846_793_005)
            .wrapping_add(1_442_695_040_888_963_407);
        self.state
    }
}

#[test]
#[ignore = "run explicitly through the bounded native soak recipe"]
fn configurable_multiplayer_process_soak() {
    let configured_seconds = env::var("GATHERMESH_SOAK_SECONDS")
        .ok()
        .and_then(|value| value.parse::<u64>().ok())
        .unwrap_or(3_600)
        .clamp(5, 86_400);
    let seed = env::var("GATHERMESH_SOAK_SEED")
        .ok()
        .and_then(|value| value.parse::<u64>().ok())
        .unwrap_or(1_311_767_462_028_172_048);
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_nanos();
    let root = env::temp_dir().join(format!("gathermesh-soak-{stamp}"));
    fs::create_dir_all(&root).expect("soak root should be creatable");
    let binary = env!("CARGO_BIN_EXE_gathermesh-node");
    let ticket = root.join("ticket.txt");
    let host_store = root.join("host");
    let host_observed = root.join("soak-host-observed");
    let host_seconds = configured_seconds.saturating_add(10).to_string();
    let host_payload = format!("soak-host-seed-{seed}-0");
    let first_peer_payload = format!("soak-peer-b-seed-{seed}-0");
    let mut host = spawn_node(
        binary,
        &[
            "--mode",
            "create",
            "--character",
            "soak-host",
            "--storage",
            host_store.to_str().expect("UTF-8 storage path"),
            "--ticket-file",
            ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            &host_payload,
            "--expect-payload",
            &first_peer_payload,
            "--observed-file",
            host_observed.to_str().expect("UTF-8 observation path"),
            "--seconds",
            &host_seconds,
        ],
    );
    wait_for_file(&ticket);

    let mut peer_a = spawn_peer(
        binary,
        &root,
        &ticket,
        "soak-peer-a",
        PeerLaunch {
            seed,
            round: 0,
            seconds: &host_seconds,
            host_payload: &host_payload,
        },
    );
    let mut peer_b = spawn_peer(
        binary,
        &root,
        &ticket,
        "soak-peer-b",
        PeerLaunch {
            seed,
            round: 0,
            seconds: &host_seconds,
            host_payload: &host_payload,
        },
    );

    let deadline = Instant::now() + Duration::from_secs(configured_seconds);
    let mut schedule = Schedule::new(seed);
    let mut round = 1u64;
    while Instant::now() < deadline {
        thread::sleep(Duration::from_secs(5));
        if Instant::now() >= deadline {
            break;
        }
        if schedule.next() & 1 == 0 {
            let _ = peer_a.kill();
            let status = peer_a.wait().expect("restarted peer should be reapable");
            assert!(
                status.success() || status.code().is_none(),
                "peer A restart must not report a native failure: seed={seed} round={round} status={status:?}"
            );
            peer_a = spawn_peer(
                binary,
                &root,
                &ticket,
                "soak-peer-a",
                PeerLaunch {
                    seed,
                    round,
                    seconds: &host_seconds,
                    host_payload: &host_payload,
                },
            );
        }
        if schedule.next() & 1 == 0 {
            let _ = peer_b.kill();
            let status = peer_b.wait().expect("stable peer should be reapable");
            assert!(
                status.success() || status.code().is_none(),
                "peer B restart must not report a native failure: seed={seed} round={round} status={status:?}"
            );
            peer_b = spawn_peer(
                binary,
                &root,
                &ticket,
                "soak-peer-b",
                PeerLaunch {
                    seed,
                    round,
                    seconds: &host_seconds,
                    host_payload: &host_payload,
                },
            );
        }
        round = round.saturating_add(1);
    }

    wait_for_file(&host_observed);
    wait_for_file(&root.join("soak-peer-a-observed"));
    wait_for_file(&root.join("soak-peer-b-observed"));

    for peer in [&mut peer_a, &mut peer_b] {
        let _ = peer.kill();
        let status = peer.wait().expect("peer process should be reapable");
        assert!(
            status.success() || status.code().is_none(),
            "peer process must not report a native failure: seed={seed} rounds={round} status={status:?}"
        );
    }
    assert!(
        host_observed.exists(),
        "host must observe the stable peer through the soak: seed={seed} rounds={round}"
    );
    assert!(
        root.join("soak-peer-a-observed").exists(),
        "restarted peer must reconcile the host register: seed={seed} rounds={round}"
    );
    assert!(
        root.join("soak-peer-b-observed").exists(),
        "stable peer must reconcile the host register: seed={seed} rounds={round}"
    );
    let host_status = host.wait().expect("host process should be reapable");
    assert!(
        host_status.success(),
        "host process must finish without a native failure: seed={seed} rounds={round} status={host_status:?}"
    );
    fs::write(
        root.join("events.txt"),
        format!(
            "seed={seed} rounds={round} durationSeconds={configured_seconds} relayMode={} relayUrlConfigured={} scheduleState={}\n",
            env::var("GATHERMESH_SOAK_RELAY_MODE").unwrap_or_default(),
            !env::var("GATHERMESH_SOAK_RELAY_URL").unwrap_or_default().is_empty(),
            schedule.state,
        ),
    )
    .expect("soak summary should be persisted");
    println!("gathermesh-soak seed={seed} rounds={round} durationSeconds={configured_seconds}");
    let _ = fs::remove_dir_all(root);
}

fn spawn_peer(
    binary: &str,
    root: &std::path::Path,
    ticket: &std::path::Path,
    name: &str,
    launch: PeerLaunch<'_>,
) -> Child {
    let store = root.join(name);
    let observed = root.join(format!("{name}-observed"));
    let payload = format!("{name}-seed-{}-{}", launch.seed, launch.round);
    let revision = launch.round.saturating_add(1).to_string();
    spawn_node(
        binary,
        &[
            "--mode",
            "join",
            "--character",
            name,
            "--storage",
            store.to_str().expect("UTF-8 storage path"),
            "--ticket-file",
            ticket.to_str().expect("UTF-8 ticket path"),
            "--put-payload",
            &payload,
            "--expect-payload",
            launch.host_payload,
            "--observed-file",
            observed.to_str().expect("UTF-8 observation path"),
            "--revision",
            &revision,
            "--seconds",
            launch.seconds,
        ],
    )
}

fn spawn_node(binary: &str, arguments: &[&str]) -> Child {
    let mut command = Command::new(binary);
    command.args(arguments);
    if let Ok(mode) = env::var("GATHERMESH_SOAK_RELAY_MODE")
        && !mode.is_empty()
    {
        command.args(["--relay-mode", mode.as_str()]);
    }
    if let Ok(url) = env::var("GATHERMESH_SOAK_RELAY_URL")
        && !url.is_empty()
    {
        command.args(["--relay-url", url.as_str()]);
    }
    command
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("native soak process should spawn")
}

fn wait_for_file(path: &std::path::Path) {
    for _ in 0..120 {
        if path.exists() {
            return;
        }
        thread::sleep(Duration::from_millis(100));
    }
    panic!(
        "expected native soak output was not created: {}",
        path.display()
    );
}
