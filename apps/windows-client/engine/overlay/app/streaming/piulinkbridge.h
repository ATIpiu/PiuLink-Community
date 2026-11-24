#pragma once

#include "video/decoder.h"

void PiuLinkBridgeStart(int negotiatedCeilingKbps);
void PiuLinkBridgeStop();
void PiuLinkBridgeAddReceivedBytes(int bytes);
void PiuLinkBridgePublishVideoStats(const VIDEO_STATS& stats, int videoFormat);
