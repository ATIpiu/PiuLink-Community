# Third-party components

The Windows Host integrates a modified Sunshine engine. The Windows Client integrates Moonlight Qt and moonlight-common-c. See the upstream pins, cumulative patches and package notices under [Host engine](../apps/stream-engine/README.md) and [Windows Client engine](../apps/windows-client/engine/upstream.json). Android and iOS derivatives retain the respective Moonlight licenses. Android also retains the Skylark notice.

Direct transport module versions and third-party texts are under [packages/direct-tunnel](../packages/direct-tunnel). The microphone driver patch carries its own license under [the driver sources](../apps/windows-host/audio-driver). These notes identify components; corresponding source and dependency notices for a distributed binary must match its release exactly.


The bundled Android OpenSSL 1.1.1q libraries use the [OpenSSL and Original SSLeay notices](../licenses/OpenSSL-1.1.1q.txt). This product includes software developed by the OpenSSL Project for use in the OpenSSL Toolkit, cryptographic software written by Eric Young, and software written by Tim Hudson. For Opus see the [Opus 1.5.2 notice](../licenses/Opus-1.5.2.txt). The Gradle wrapper is distributed with the [Gradle 8.13 license and notices](../licenses/Gradle-8.13.txt).
