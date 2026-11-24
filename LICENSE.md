# PiuLink licensing

Required Notice: Copyright © 2026 ATIPiu Labs / Ryan.

PiuLink is a mixed-license distribution. The following terms apply to the respective components.

| Component | License |
| --- | --- |
| Independent original PiuLink programs and materials, including the rendezvous service, host management applications, standalone client front ends and their documentation, except where a more specific license applies | [PolyForm Noncommercial License 1.0.0](licenses/PolyForm-Noncommercial-1.0.0.txt). For uses outside the permissions of that license, obtain separate permission from the copyright holder. |
| Moonlight Android/Skylark derivative in `piulink-android/`, Moonlight iOS patches, modified Sunshine and Moonlight Qt engine code and patches, the Windows native engine linked with moonlight-common-c, and PiuLink-authored code incorporated into these GPL works | [GNU GPL version 3](piulink-android/LICENSE.txt), subject to existing upstream notices and separately licensed dependencies. This includes PiuLink's Go transport sources and mobile bindings used to build the Android application's linked AAR. Recipients retain all GPL rights, including commercial use and redistribution under its terms. |
| Microphone driver modifications based on Microsoft's sample | [Microsoft Public License](apps/windows-host/audio-driver/Microsoft-Public-License.txt) as documented in the driver sources. |
| Third-party materials | Their respective licenses continue to apply, including those of Opus, OpenSSL, enet, the Gradle wrapper and Go dependencies. See [third-party notices](compliance/THIRD_PARTY_NOTICES.md). |

A license or copyright notice attached to a particular component takes precedence for that component. The noncommercial terms apply only to independently licensable PiuLink material. They impose no additional condition on any GPL-covered work or its corresponding source. Public access to the repository includes both the noncommercial source-available components and the free/open-source components.

中文说明：PiuLink 自有的独立组件按 PolyForm Noncommercial 1.0.0 提供。超出许可范围的商业使用需要另行获得授权。Moonlight、Sunshine 衍生程序及其对应源码按 GPLv3 分发，使用者保有 GPL 赋予的商业使用和再分发权利。第三方依赖继续适用各自许可。
