#include "piulinkbridge.h"

#include <Limelight.h>
#include <SDL.h>

#ifdef Q_OS_WIN32
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

namespace {
struct BridgeStats {
    double fps = 0;
    double networkDroppedPercent = 0;
    double jitterDroppedPercent = 0;
    int rtt = 0;
    int rttVariance = 0;
    double decodeMs = 0;
    double renderMs = 0;
    char codec[32] = "unknown";
};

HANDLE pipeHandle = INVALID_HANDLE_VALUE;
HANDLE workerHandle = nullptr;
std::atomic<bool> stopping(false);
std::atomic<long long> receivedBytes(0);
CRITICAL_SECTION statsLock;
BridgeStats latestStats;
int ceilingKbps = 0;
int autoTargetKbps = 0;
bool adaptiveRequested = false;
bool bitrateRequested = false;
bool adaptiveConfigured = false;
DWORD lastBitrateAttempt = 0;
DWORD lastAdaptiveAttempt = 0;
long long sequence = 0;

const char* codecName(int format) {
    switch (format) {
    case VIDEO_FORMAT_H264: return "H.264";
    case VIDEO_FORMAT_H264_HIGH8_444: return "H.264 4:4:4";
    case VIDEO_FORMAT_H265: return "HEVC";
    case VIDEO_FORMAT_H265_REXT8_444: return "HEVC 4:4:4";
    case VIDEO_FORMAT_H265_MAIN10: return "HEVC 10-bit";
    case VIDEO_FORMAT_H265_REXT10_444: return "HEVC 10-bit 4:4:4";
    case VIDEO_FORMAT_AV1_MAIN8: return "AV1";
    case VIDEO_FORMAT_AV1_HIGH8_444: return "AV1 4:4:4";
    case VIDEO_FORMAT_AV1_MAIN10: return "AV1 10-bit";
    case VIDEO_FORMAT_AV1_HIGH10_444: return "AV1 10-bit 4:4:4";
    default: return "unknown";
    }
}

bool parseValue(const std::string& line, const char* command, int& value) {
    std::string marker = std::string("\"command\":\"") + command + "\"";
    if (line.find(marker) == std::string::npos) return false;
    auto valueAt = line.find("\"value\":");
    if (valueAt == std::string::npos) return false;
    value = std::atoi(line.c_str() + valueAt + 8);
    return true;
}

void handleCommand(const std::string& line) {
    int value;
    if (parseValue(line, "setBitrateKbps", value) && value >= 500 && value <= 500000) {
        autoTargetKbps = (std::min)(value, ceilingKbps);
        bitrateRequested = true;
        lastBitrateAttempt = GetTickCount();
        LiSendPiuLinkControl(1, autoTargetKbps);
    }
    else if (parseValue(line, "setContentAdaptive", value) && (value == 0 || value == 1)) {
        adaptiveRequested = value != 0;
        adaptiveConfigured = true;
        lastAdaptiveAttempt = GetTickCount();
        LiSendPiuLinkControl(3, value);
    }
}

void sendTelemetry(DWORD elapsedMs) {
    BridgeStats stats;
    EnterCriticalSection(&statsLock);
    stats = latestStats;
    LeaveCriticalSection(&statsLock);

    const auto bytes = receivedBytes.exchange(0);
    const double receivedMbps = elapsedMs ? bytes * 8.0 / elapsedMs / 1000.0 : 0;
    const int bitrateAck = LiGetPiuLinkAck(1);
    const int adaptiveAck = LiGetPiuLinkAck(3);
    const DWORD now = GetTickCount();
    if (bitrateRequested && bitrateAck < 0 && now - lastBitrateAttempt >= 2000) {
        lastBitrateAttempt = now;
        LiSendPiuLinkControl(1, autoTargetKbps);
    }
    if (adaptiveConfigured && adaptiveAck < 0 && now - lastAdaptiveAttempt >= 2000) {
        lastAdaptiveAttempt = now;
        LiSendPiuLinkControl(3, adaptiveRequested ? 1 : 0);
    }
    const bool adaptiveConfirmed = adaptiveAck == (adaptiveRequested ? 2 : 1);
    const int engineTarget = bitrateAck > 0 ? bitrateAck : autoTargetKbps;
    const char* adaptiveState = !adaptiveRequested ? (adaptiveConfirmed ? "off" : "pending")
        : !adaptiveConfirmed ? "pending"
        : engineTarget > 0 && engineTarget < autoTargetKbps ? "static-reduced" : "active-full";

    char json[2048];
    int length = std::snprintf(json, sizeof(json),
        "{\"protocolVersion\":1,\"sequence\":%lld,\"state\":\"streaming\","
        "\"codec\":\"%s\",\"negotiatedCeilingKbps\":%d,\"autoTargetKbps\":%d,"
        "\"engineTargetKbps\":%d,\"receivedMbps\":%.3f,\"contentAdaptiveRequested\":%s,"
        "\"contentAdaptiveConfirmed\":%s,\"contentAdaptiveState\":\"%s\","
        "\"framesPerSecond\":%.3f,\"networkDroppedPercent\":%.3f,"
        "\"jitterDroppedPercent\":%.3f,\"roundTripTimeMs\":%d,"
        "\"roundTripVarianceMs\":%d,\"decodeTimeMs\":%.3f,\"renderTimeMs\":%.3f}\n",
        ++sequence, stats.codec, ceilingKbps, autoTargetKbps, engineTarget, receivedMbps,
        adaptiveRequested ? "true" : "false", adaptiveConfirmed ? "true" : "false", adaptiveState,
        stats.fps, stats.networkDroppedPercent, stats.jitterDroppedPercent, stats.rtt,
        stats.rttVariance, stats.decodeMs, stats.renderMs);
    if (length > 0 && length < static_cast<int>(sizeof(json))) {
        DWORD written;
        WriteFile(pipeHandle, json, static_cast<DWORD>(length), &written, nullptr);
    }
}

DWORD WINAPI workerProc(void*) {
    char pipeName[256];
    DWORD size = GetEnvironmentVariableA("PIULINK_ENGINE_PIPE", pipeName, sizeof(pipeName));
    if (size == 0 || size >= sizeof(pipeName)) return 0;
    std::string fullName = "\\\\.\\pipe\\" + std::string(pipeName);
    for (int attempt = 0; attempt < 100 && !stopping; attempt++) {
        pipeHandle = CreateFileA(fullName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                 OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipeHandle != INVALID_HANDLE_VALUE) break;
        Sleep(100);
    }
    if (pipeHandle == INVALID_HANDLE_VALUE) return 0;

    std::string input;
    DWORD lastTelemetry = GetTickCount();
    while (!stopping) {
        DWORD available = 0;
        if (!PeekNamedPipe(pipeHandle, nullptr, 0, nullptr, &available, nullptr)) break;
        if (available) {
            char buffer[1024];
            DWORD read = 0;
            if (!ReadFile(pipeHandle, buffer, (std::min<DWORD>)(available, sizeof(buffer)), &read, nullptr)) break;
            input.append(buffer, read);
            size_t newline;
            while ((newline = input.find('\n')) != std::string::npos) {
                handleCommand(input.substr(0, newline));
                input.erase(0, newline + 1);
            }
            if (input.size() > 65536) input.clear();
        }
        DWORD now = GetTickCount();
        DWORD elapsed = now - lastTelemetry;
        if (elapsed >= 1000) {
            sendTelemetry(elapsed);
            lastTelemetry = now;
        }
        Sleep(20);
    }
    CloseHandle(pipeHandle);
    pipeHandle = INVALID_HANDLE_VALUE;
    return 0;
}
}

void PiuLinkBridgeStart(int negotiatedCeilingKbps) {
    if (workerHandle) return;
    ceilingKbps = negotiatedCeilingKbps;
    autoTargetKbps = negotiatedCeilingKbps;
    adaptiveRequested = false;
    bitrateRequested = false;
    adaptiveConfigured = false;
    lastBitrateAttempt = lastAdaptiveAttempt = 0;
    sequence = 0;
    receivedBytes = 0;
    stopping = false;
    InitializeCriticalSection(&statsLock);
    workerHandle = CreateThread(nullptr, 0, workerProc, nullptr, 0, nullptr);
}

void PiuLinkBridgeStop() {
    if (!workerHandle) return;
    stopping = true;
    if (pipeHandle != INVALID_HANDLE_VALUE) CancelIoEx(pipeHandle, nullptr);
    WaitForSingleObject(workerHandle, 3000);
    CloseHandle(workerHandle);
    workerHandle = nullptr;
    DeleteCriticalSection(&statsLock);
}

void PiuLinkBridgeAddReceivedBytes(int bytes) { receivedBytes += bytes; }

void PiuLinkBridgePublishVideoStats(const VIDEO_STATS& stats, int videoFormat) {
    BridgeStats snapshot;
    snapshot.fps = stats.renderedFps;
    snapshot.networkDroppedPercent = stats.totalFrames ? stats.networkDroppedFrames * 100.0 / stats.totalFrames : 0;
    snapshot.jitterDroppedPercent = stats.decodedFrames ? stats.pacerDroppedFrames * 100.0 / stats.decodedFrames : 0;
    snapshot.rtt = stats.lastRtt;
    snapshot.rttVariance = stats.lastRttVariance;
    snapshot.decodeMs = stats.decodedFrames ? static_cast<double>(stats.totalDecodeTime) / stats.decodedFrames : 0;
    snapshot.renderMs = stats.renderedFrames ? static_cast<double>(stats.totalRenderTime) / stats.renderedFrames : 0;
    std::strncpy(snapshot.codec, codecName(videoFormat), sizeof(snapshot.codec) - 1);
    EnterCriticalSection(&statsLock);
    latestStats = snapshot;
    LeaveCriticalSection(&statsLock);
}

#else
void PiuLinkBridgeStart(int) {}
void PiuLinkBridgeStop() {}
void PiuLinkBridgeAddReceivedBytes(int) {}
void PiuLinkBridgePublishVideoStats(const VIDEO_STATS&, int) {}
#endif
