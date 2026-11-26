# Notices

PiuLink Android is based on Moonlight Android and Moonlight Skylark and is distributed under GNU GPL version 3. The full license is in `LICENSE.txt`.

This public tree includes the complete modified moonlight-common-c native source and its pinned enet dependency. Preserve each submodule's license and copyright notices in source and binary distributions.

PiuLink opens or links to the separately installed official Tailscale Android application. Tailscale software is not bundled in this repository. “Tailscale” is used only to identify compatibility with the third-party product.

Sunshine runs separately on the user's Windows computer and is not bundled in this repository.

## Android 0.3.206 runtime dependencies

The release resolves the following Maven artifacts for the nonRoot release runtime classpath. Native inputs are pinned in the source tree and the Go transport dependencies in [go.mod](../packages/direct-tunnel/go.mod). The Protobuf runtime license is included in the APK and in the [source distribution](../licenses/Protobuf-3.25.5.txt).

- `com.github.cgutman:ShieldControllerExtensions:1.0.1`
- `com.github.mwiede:jsch:2.28.0`
- `com.google.protobuf:protobuf-java:3.25.5`
- `com.squareup.okhttp3:okhttp:4.12.0`
- `com.squareup.okio:okio-jvm:3.6.0`
- `com.squareup.okio:okio:3.6.0`
- `org.bouncycastle:bcpkix-jdk18on:1.84`
- `org.bouncycastle:bcprov-jdk18on:1.84`
- `org.bouncycastle:bcutil-jdk18on:1.84`
- `org.jcodec:jcodec:0.2.5`
- `org.jetbrains.kotlin:kotlin-stdlib-common:1.9.10`
- `org.jetbrains.kotlin:kotlin-stdlib-jdk7:1.9.10`
- `org.jetbrains.kotlin:kotlin-stdlib-jdk8:1.9.10`
- `org.jetbrains.kotlin:kotlin-stdlib:1.9.10`
- `org.jetbrains:annotations:13.0`
- `org.jmdns:jmdns:3.5.9`
- `org.slf4j:slf4j-api:2.0.7`
