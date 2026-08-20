use gathermesh_ffi::{
    gbm_abi_version, gbm_buffer, gbm_buffer_free, gbm_create, gbm_destroy, gbm_error_free,
    gbm_error_message, gbm_event, gbm_event_free, gbm_poll_event, gbm_put, gbm_result,
    gbm_shutdown, gbm_snapshot_destroy, gbm_start,
};

fn valid_config() -> Vec<u8> {
    br#"{"schema_version":1,"storage_directory":"target/ffi-contract","event_capacity":16,"command_capacity":16,"max_key_bytes":4096,"max_value_bytes":1048576,"relay_mode":0,"relay_urls":[]}"#.to_vec()
}

const RECORD_ID: &[u8] = b"00000000-0000-0000-0000-000000000001";

#[test]
fn abi_rejects_invalid_handles_and_null_outputs_without_unwinding() {
    assert_eq!(gbm_abi_version(), 2);
    assert_ne!(gbm_start(0).code, 0);
    // SAFETY: null is intentionally passed to exercise the ABI's validation path.
    assert_ne!(unsafe { gbm_poll_event(0, std::ptr::null_mut()) }.code, 0);
    assert_ne!(
        // SAFETY: null zero-length pointers intentionally exercise the validation path.
        unsafe {
            gbm_put(
                0,
                RECORD_ID.as_ptr(),
                RECORD_ID.len(),
                std::ptr::null(),
                0,
                b"worker-session".as_ptr(),
                b"worker-session".len(),
                0,
                0,
                1,
                std::ptr::null(),
                0,
            )
        }
        .code,
        0
    );
}

#[test]
fn free_operations_are_safe_for_empty_buffers_and_repeated_snapshot_destroy() {
    gbm_buffer_free(gbm_buffer::default());
    gbm_event_free(gbm_event::default());
    assert_eq!(gbm_snapshot_destroy(0).code, 0);
    assert_eq!(gbm_destroy(u64::MAX).code, 0);
}

#[test]
fn abi_rejects_truncated_or_unknown_config_before_service_creation() {
    let config = br#"{}"#;
    let mut handle = 0;
    // SAFETY: config and handle point to live local storage for this synchronous call.
    assert_eq!(
        unsafe { gbm_create(config.as_ptr(), config.len(), &mut handle) }.code,
        1
    );

    let config = br#"{"schema_version":999,"storage_directory":"target/ffi-contract","event_capacity":16,"command_capacity":16,"max_key_bytes":4096,"max_value_bytes":1048576,"relay_mode":0,"relay_urls":[]}"#;
    // SAFETY: config and handle point to live local storage for this synchronous call.
    assert_eq!(
        unsafe { gbm_create(config.as_ptr(), config.len(), &mut handle) }.code,
        2
    );
}

#[test]
fn abi_rejects_nonzero_length_null_input_pointer() {
    let mut handle = 0;
    let config = valid_config();
    // SAFETY: config and handle point to live local storage for this synchronous call.
    assert_eq!(
        unsafe { gbm_create(config.as_ptr(), config.len(), &mut handle) }.code,
        0
    );
    assert_eq!(
        // SAFETY: null nonzero-length pointer intentionally exercises validation.
        unsafe {
            gbm_put(
                handle,
                RECORD_ID.as_ptr(),
                RECORD_ID.len(),
                std::ptr::null(),
                1,
                b"worker-session".as_ptr(),
                b"worker-session".len(),
                0,
                0,
                1,
                std::ptr::null(),
                0,
            )
        }
        .code,
        1
    );
    assert_eq!(gbm_destroy(handle).code, 0);
    assert_eq!(gbm_destroy(handle).code, 0);
}

#[test]
fn config_json_rejects_unknown_duplicate_and_invalid_utf8_fields() {
    let cases: &[&[u8]] = &[
        br#"{"schema_version":1,"storage_directory":"target/ffi-contract","event_capacity":16,"command_capacity":16,"max_key_bytes":4096,"max_value_bytes":1048576,"relay_mode":0,"relay_urls":[],"unknown":1}"#,
        br#"{"schema_version":1,"schema_version":1,"storage_directory":"target/ffi-contract","event_capacity":16,"command_capacity":16,"max_key_bytes":4096,"max_value_bytes":1048576,"relay_mode":0,"relay_urls":[]}"#,
        br#"{"schema_version":1,"storage_directory":"target/ffi-contract","event_capacity":16,"command_capacity":16,"max_key_bytes":4096,"max_value_bytes":1048576,"relay_mode":0,"relay_urls":["\uD800"]}"#,
    ];
    for config in cases {
        let mut handle = 0;
        // SAFETY: config and handle point to live local storage for this synchronous call.
        assert_ne!(
            unsafe { gbm_create(config.as_ptr(), config.len(), &mut handle) }.code,
            0
        );
    }
}

#[test]
fn shutdown_closes_admission_and_rejects_late_puts() {
    let config = valid_config();
    let mut handle = 0;
    // SAFETY: config and handle point to live local storage for this synchronous call.
    assert_eq!(
        unsafe { gbm_create(config.as_ptr(), config.len(), &mut handle) }.code,
        0
    );
    assert_eq!(gbm_shutdown(handle, 1).code, 0);
    let key = b"v1/workers/not-an-author";
    // SAFETY: all pointers refer to live test buffers for this synchronous call.
    let result = unsafe {
        gbm_put(
            handle,
            RECORD_ID.as_ptr(),
            RECORD_ID.len(),
            key.as_ptr(),
            key.len(),
            b"worker-session".as_ptr(),
            b"worker-session".len(),
            0,
            0,
            1,
            std::ptr::null(),
            0,
        )
    };
    assert_eq!(result.code, 8, "late commands must see GBM_E_CLOSING");
    assert_ne!(gbm_shutdown(handle, 300_001).code, 0);
    assert_eq!(gbm_destroy(handle).code, 0);
}

#[test]
fn concurrent_shutdown_put_destroy_has_no_use_after_destroy_result() {
    let config = valid_config();
    let mut handle = 0;
    // SAFETY: config and handle point to live local storage for this synchronous call.
    assert_eq!(
        unsafe { gbm_create(config.as_ptr(), config.len(), &mut handle) }.code,
        0
    );
    let put_thread = std::thread::spawn(move || {
        let key = b"v1/workers/not-an-author";
        // SAFETY: all pointers refer to live static test buffers for this synchronous call.
        unsafe {
            gbm_put(
                handle,
                RECORD_ID.as_ptr(),
                RECORD_ID.len(),
                key.as_ptr(),
                key.len(),
                b"worker-session".as_ptr(),
                b"worker-session".len(),
                0,
                0,
                1,
                std::ptr::null(),
                0,
            )
        }
    });
    let shutdown_thread = std::thread::spawn(move || gbm_shutdown(handle, 1));
    let destroy_thread = std::thread::spawn(move || gbm_destroy(handle));
    let _ = put_thread.join().expect("put thread must not panic");
    let _ = shutdown_thread
        .join()
        .expect("shutdown thread must not panic");
    let _ = destroy_thread
        .join()
        .expect("destroy thread must not panic");
    assert_eq!(gbm_destroy(handle).code, 0);
}

#[test]
fn put_rejects_non_utf8_and_oversized_record_types_at_the_abi_boundary() {
    let config = valid_config();
    let mut handle = 0;
    // SAFETY: config and handle point to live local storage for this synchronous call.
    assert_eq!(
        unsafe { gbm_create(config.as_ptr(), config.len(), &mut handle) }.code,
        0
    );
    let malformed_id = b"not-a-guid";
    // SAFETY: all pointers refer to live test buffers for this synchronous call.
    assert_eq!(
        unsafe {
            gbm_put(
                handle,
                malformed_id.as_ptr(),
                malformed_id.len(),
                std::ptr::null(),
                0,
                b"worker-session".as_ptr(),
                b"worker-session".len(),
                0,
                0,
                1,
                std::ptr::null(),
                0,
            )
        }
        .code,
        12
    );
    let invalid_type = [0xff, 0xfe];
    // SAFETY: all pointers refer to live test buffers for this synchronous call.
    assert_eq!(
        unsafe {
            gbm_put(
                handle,
                RECORD_ID.as_ptr(),
                RECORD_ID.len(),
                std::ptr::null(),
                0,
                invalid_type.as_ptr(),
                invalid_type.len(),
                0,
                0,
                1,
                std::ptr::null(),
                0,
            )
        }
        .code,
        1
    );
    let oversized_type = [b'x'; 129];
    // SAFETY: all pointers refer to live test buffers for this synchronous call.
    assert_eq!(
        unsafe {
            gbm_put(
                handle,
                RECORD_ID.as_ptr(),
                RECORD_ID.len(),
                std::ptr::null(),
                0,
                oversized_type.as_ptr(),
                oversized_type.len(),
                0,
                0,
                1,
                std::ptr::null(),
                0,
            )
        }
        .code,
        11
    );
    assert_eq!(gbm_destroy(handle).code, 0);
}

#[test]
fn error_message_buffer_has_explicit_owned_lifetime() {
    let result = gbm_start(0);
    assert_ne!(result.error_id, 0);
    let mut message = gbm_buffer::default();
    // SAFETY: message points to live writable local storage for this synchronous call.
    assert_eq!(
        unsafe { gbm_error_message(result.error_id, &mut message) }.code,
        0
    );
    assert!(message.len > 0);
    gbm_buffer_free(message);
    gbm_error_free(result.error_id);
}

#[allow(dead_code)]
fn _result_is_blittable(result: gbm_result) -> (u32, u32, u64) {
    (result.code, result.flags, result.error_id)
}
