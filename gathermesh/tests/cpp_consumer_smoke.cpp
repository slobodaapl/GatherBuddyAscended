#include <cstddef>
#include <cstdint>

extern "C" {
using gbm_handle = std::uint64_t;

struct gbm_result {
    std::uint32_t code;
    std::uint32_t flags;
    std::uint64_t error_id;
};

std::uint32_t gbm_abi_version() noexcept;
gbm_result gbm_create(const std::uint8_t* config, std::size_t config_len,
                      gbm_handle* out_handle) noexcept;
gbm_result gbm_destroy(gbm_handle handle) noexcept;
}

template <typename Call>
gbm_result invoke_abi_boundary(Call&& call) noexcept {
    try {
        return call();
    } catch (...) {
        // A throwing C++ dependency is converted to the native panic status before any C ABI
        // call can observe it. No C++ exception is permitted to cross the adapter.
        return gbm_result{17, 0, 0};
    }
}

int run_smoke() {
    const auto dependency_failure = invoke_abi_boundary([]() -> gbm_result {
        throw 42;
    });
    if (dependency_failure.code != 17 || dependency_failure.error_id != 0) {
        return 1;
    }

    if (gbm_abi_version() != 2) {
        return 2;
    }

    gbm_handle handle = 0;
    const auto invalid = gbm_create(nullptr, 0, &handle);
    if (invalid.code != 1 || invalid.error_id == 0) {
        return 3;
    }

    constexpr char config[] =
        R"({"schema_version":1,"storage_directory":"target/cpp-consumer-smoke","event_capacity":16,"command_capacity":16,"max_key_bytes":4096,"max_value_bytes":1048576,"relay_mode":1,"relay_urls":[]})";
    const auto created = gbm_create(
        reinterpret_cast<const std::uint8_t*>(config), sizeof(config) - 1, &handle);
    if (created.code != 0 || handle == 0) {
        return 4;
    }
    const auto destroyed = gbm_destroy(handle);
    return destroyed.code == 0 ? 0 : 5;
}

int main() noexcept {
    try {
        return run_smoke();
    } catch (...) {
        // A C++ exception must terminate at this consumer boundary, never
        // cross into Rust or the managed plugin.
        return 99;
    }
}
