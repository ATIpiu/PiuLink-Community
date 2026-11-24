cmake_minimum_required(VERSION 3.20)
project(PiuLinkNativeEngine LANGUAGES C CXX)
set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")

set(MOONLIGHT_COMMON "${CMAKE_CURRENT_LIST_DIR}/../work/moonlight-qt/moonlight-common-c/moonlight-common-c")
set(MOONLIGHT_LIBS "${CMAKE_CURRENT_LIST_DIR}/../work/moonlight-qt/libs/windows")
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
add_subdirectory("${MOONLIGHT_COMMON}/enet" enet)
file(GLOB COMMON_SOURCES "${MOONLIGHT_COMMON}/src/*.c" "${MOONLIGHT_COMMON}/reedsolomon/*.c")
add_library(moonlight-common-c STATIC ${COMMON_SOURCES})
target_include_directories(moonlight-common-c PUBLIC "${MOONLIGHT_COMMON}/src" PRIVATE
  "${MOONLIGHT_COMMON}/reedsolomon" "${MOONLIGHT_LIBS}/include" "${MOONLIGHT_LIBS}/include/x64")
target_compile_definitions(moonlight-common-c PRIVATE HAS_SOCKLEN_T NDEBUG)
target_compile_options(moonlight-common-c PRIVATE /W3 /wd4100 /wd4232 /wd5105)
target_link_libraries(moonlight-common-c PRIVATE enet "${MOONLIGHT_LIBS}/lib/x64/libcrypto.lib" ws2_32 winmm)

add_library(PiuLink.Native SHARED src/bridge.cpp)
target_include_directories(PiuLink.Native PRIVATE
  "${MOONLIGHT_COMMON}/src" "${MOONLIGHT_LIBS}/include" "${MOONLIGHT_LIBS}/include/x64")
target_link_directories(PiuLink.Native PRIVATE "${MOONLIGHT_LIBS}/lib/x64")
target_link_libraries(PiuLink.Native PRIVATE moonlight-common-c SDL2 avcodec avutil swscale opus
  ws2_32 winmm user32 gdi32 d3d11 dxgi)
target_compile_definitions(PiuLink.Native PRIVATE SDL_MAIN_HANDLED NOMINMAX _CRT_SECURE_NO_WARNINGS PIULINK_NATIVE_EXPORTS)

install(TARGETS PiuLink.Native RUNTIME DESTINATION .)
install(FILES
  "${MOONLIGHT_LIBS}/lib/x64/SDL2.dll"
  "${MOONLIGHT_LIBS}/lib/x64/avcodec-61.dll"
  "${MOONLIGHT_LIBS}/lib/x64/dav1d.dll"
  "${MOONLIGHT_LIBS}/lib/x64/avutil-59.dll"
  "${MOONLIGHT_LIBS}/lib/x64/swscale-8.dll"
  "${MOONLIGHT_LIBS}/lib/x64/opus.dll"
  "${MOONLIGHT_LIBS}/lib/x64/libcrypto-3-x64.dll"
  DESTINATION .)
install(FILES "${MOONLIGHT_COMMON}/LICENSE.txt" DESTINATION . RENAME moonlight-common-c-LICENSE.txt)
install(FILES "${CMAKE_CURRENT_LIST_DIR}/../installers/licenses/Moonlight-NOTICE.md" DESTINATION .)
