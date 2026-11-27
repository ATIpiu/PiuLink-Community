# OHOS_ARCH is selected by the SDK toolchain separately for each HAP ABI.
if(OHOS_ARCH STREQUAL "arm64-v8a")
    set(PIULINK_TARGET aarch64-unknown-linux-ohos)
elseif(OHOS_ARCH STREQUAL "x86_64")
    set(PIULINK_TARGET x86_64-unknown-linux-ohos)
elseif(OHOS_ARCH STREQUAL "armeabi-v7a")
    set(PIULINK_TARGET armv7-unknown-linux-ohos)
else()
    message(FATAL_ERROR "Unsupported PiuLink OS architecture: ${OHOS_ARCH}")
endif()
find_program(PIULINK_CARGO cargo HINTS "$ENV{HOME}/.cargo/bin" "$ENV{USERPROFILE}/.cargo/bin" REQUIRED)
set(PIULINK_MANIFEST "${CMAKE_CURRENT_LIST_DIR}/rust/portable/Cargo.toml")
set(PIULINK_ARCHIVE "${CMAKE_CURRENT_BINARY_DIR}/rust/${PIULINK_TARGET}/release/libpiulink_stream_core.a")
file(GLOB PIULINK_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_LIST_DIR}/rust/wire/*.rs"
    "${CMAKE_CURRENT_LIST_DIR}/rust/portable/*.rs")
add_custom_command(OUTPUT "${PIULINK_ARCHIVE}"
    COMMAND "${PIULINK_CARGO}" build --locked --release --target "${PIULINK_TARGET}"
            --manifest-path "${PIULINK_MANIFEST}" --target-dir "${CMAKE_CURRENT_BINARY_DIR}/rust"
    DEPENDS "${PIULINK_MANIFEST}" "${CMAKE_CURRENT_LIST_DIR}/rust/portable/Cargo.lock" ${PIULINK_SOURCES}
    VERBATIM)
add_custom_target(piulink_rust_archive DEPENDS "${PIULINK_ARCHIVE}")
add_dependencies(moonlight_nativelib piulink_rust_archive)
target_link_libraries(moonlight_nativelib PRIVATE "${PIULINK_ARCHIVE}")
