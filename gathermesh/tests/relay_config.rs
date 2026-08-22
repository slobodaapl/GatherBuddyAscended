use gathermesh_ffi::{gbm_create, gbm_destroy};

fn config(relay_mode: u32, relay_urls: &str) -> Vec<u8> {
    format!(
        "{{\"schema_version\":1,\"storage_directory\":\"target/relay-config\",\"event_capacity\":16,\"command_capacity\":16,\"max_key_bytes\":4096,\"max_value_bytes\":1048576,\"relay_mode\":{relay_mode},\"relay_urls\":{relay_urls}}}"
    )
    .into_bytes()
}

#[test]
fn relay_configuration_has_sane_modes_and_bounded_urls() {
    let invalid_mode = config(3, "[]");
    let mut handle = 0;
    // SAFETY: the JSON and output handle remain valid for this synchronous ABI call.
    assert_ne!(
        unsafe { gbm_create(invalid_mode.as_ptr(), invalid_mode.len(), &mut handle) }.code,
        0
    );

    let invalid_url = config(0, "[\"\"]");
    // SAFETY: the JSON and output handle remain valid for this synchronous ABI call.
    assert_ne!(
        unsafe { gbm_create(invalid_url.as_ptr(), invalid_url.len(), &mut handle) }.code,
        0
    );

    let disabled = config(1, "[]");
    // SAFETY: the JSON and output handle remain valid for this synchronous ABI call.
    assert_eq!(
        unsafe { gbm_create(disabled.as_ptr(), disabled.len(), &mut handle) }.code,
        0
    );
    assert_eq!(gbm_destroy(handle).code, 0);

    let relay_only = config(2, "[]");
    // SAFETY: the JSON and output handle remain valid for this synchronous ABI call.
    assert_eq!(
        unsafe { gbm_create(relay_only.as_ptr(), relay_only.len(), &mut handle) }.code,
        0
    );
    assert_eq!(gbm_destroy(handle).code, 0);

    let custom = config(0, "[\"https://relay.example.invalid/\"]");
    // SAFETY: the JSON and output handle remain valid for this synchronous ABI call.
    assert_eq!(
        unsafe { gbm_create(custom.as_ptr(), custom.len(), &mut handle) }.code,
        0
    );
    assert_eq!(gbm_destroy(handle).code, 0);

    let custom_relay_only = config(2, "[\"https://relay.example.invalid/\"]");
    // SAFETY: the JSON and output handle remain valid for this synchronous ABI call.
    assert_eq!(
        unsafe {
            gbm_create(
                custom_relay_only.as_ptr(),
                custom_relay_only.len(),
                &mut handle,
            )
        }
        .code,
        0
    );
    assert_eq!(gbm_destroy(handle).code, 0);
}
