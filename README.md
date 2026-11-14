# RemoteMic

> RemoteMic 由 TheShadow178 维护，Fork 自 [vRemoter](https://github.com/VincentKingHsu/vRemoter)（MIT 许可）。
> 本 Fork 修掉了 macOS 26 上 Chromecast 遥控器按键映射失效的问题，并移除了遥测依赖。

[上游项目](https://github.com/VincentKingHsu/vRemoter) · 本 Fork 版本：`1.5.0`

## 这个 Fork 修了什么

原版 v1.1.1 里，Chromecast 遥控器的**语音键可用，但按键映射全部无效**（对应原仓库 Issue #3、#4）：

```
[CAST-HID] manager open failed: 0xE00002C1
```

`0xE00002C1` 是 `kIOReturnNotPrivileged`。根因在 macOS 的 IOHIDFamily：

```cpp
// IOHIDFamily/IOHIDLibUserClient.cpp
if (!_privilegedClient) {
    __Require_Action(!fNubIsKeyboard ||
        ((options & kIOHIDOptionsTypeSeizeDevice) == 0),
        exit, ret = kIOReturnNotPrivileged);
```

Chromecast 遥控器的 HID collection 属于 keyboard 类。非特权进程用 `kIOHIDOptionsTypeSeizeDevice` 打开它，一定拿到 `kIOReturnNotPrivileged`；改用共享打开虽然能开，却收不到按键报告，也拦不住遥控器原本的媒体键行为。`_privilegedClient` 只认两个 Apple 私有 entitlement 或 `kIOClientPrivilegeAdministrator`；本机实测 ad-hoc 签名 + 私有 entitlement 会被 AMFI 直接 SIGKILL，所以**唯一可行路径是 root**。

本 Fork 的做法：

- 新增最小化的 root 守护进程 `RemoteMicHIDHelper`（`Sources/RemoteMicHIDHelper`），由 launchd 以 root 常驻；
- 它独占（seize）`VID 0x18D1 / PID 0x9450` 的 HID collection，解析按键报告，通过 XPC 只把「按键 usage + 按下/松开」回传给 App；
- App 侧新增 `ChromecastHIDHelperClient`，启动时探测 helper；helper 不在就退回原来的进程内模式并打印原因，不会静默失效；
- helper 校验连接方进程的代码签名 identifier（`local.simaqingfeng.vRemote`），其他进程无法订阅按键；
- 移除 TelemetryDeck 依赖与 `VRTelemetryDeck*` 配置，App 不再上报任何遥测；构建不再需要联网拉依赖。

## 支持范围

语音和按键映射都依赖设备同时满足两点：BLE HID 的 VID/PID 为 `0x18D1 / 0x9450`，且支持 Google ATVV 语音服务。程序只按 VID/PID 匹配，不校验设备名。

| 遥控器版本 | 典型型号 | 支持情况 | 说明 |
|---|---|---|---|
| 原厂 Chromecast with Google TV 遥控器（2020，HD/4K 套装） | G9N9N / 10395A-G9N9N | ✅ 完整支持 | 语音 + 全部按键映射 |
| 解离版（已从原套装/原机解绑的同款遥控器） | G9N9N | ✅ 完整支持 | 「解离」只影响能否自由配对，不决定兼容性；仍需是 BLE + ATVV 版本 |
| 第三方克隆版（杰理/JL、Mecool G10 等） | 仿 G9N9N / G10 | ⚠️ 视固件 | 仅当完整仿冒 `0x18D1:0x9450` 且保留 ATVV 语音时可用；纯红外或 2.4G 版本不支持 |
| Google TV Streamer 遥控器（2024） | GA05662 | ❌ 暂不支持 | 尺寸更大、带响铃与自定义键，PID 和语音实现都不同 |
| 小米 / 其他 Android TV 遥控器 | 小米蓝牙语音遥控器 `0x2717:0x32B8` | ❌ 不支持 | VID/PID 与语音链路不同 |
| X6-Remote | `0x1D5A / 0xC081` | ✅ 保持上游行为 | 语音 + 按键映射 |

购买时确认三件事：① 是蓝牙 BLE 版，不是纯红外/2.4G；② HID 为 `18D1:9450`；③ 麦克风键能正常唤出语音（ATVV）。

macOS 按键映射需要本机安装 root helper；不装 helper 时语音仍可用，按键映射不可用（原因见上）。

## macOS 安装

```bash
./package-app.sh          # 编译 App + helper，生成 dist/build/RemoteMic.app
./install-app.sh          # 复制到 ~/Applications
sudo ./install-helper.sh  # 安装 root helper（launchd + /Library/PrivilegedHelperTools）
```

卸载 helper：

```bash
sudo ./uninstall-helper.sh
```

> 每次升级本 Fork 的 App 后，建议重跑一次 `sudo ./install-helper.sh`，让 helper 与 App 的 XPC 协议保持一致。

真机验证步骤、日志关键字、按键 usage 表与排查方法见 [docs/VERIFY-CHECKLIST.md](docs/VERIFY-CHECKLIST.md)。

## Windows 预览版

仓库现包含独立的 `.NET 8 + WPF` Windows 预览实现，界面沿用 v1.5.0 的音频/按键映射布局，并已移植 ATVV 0.4/1.0、ADPCM、语音会话协调、双击/长按手势、Windows 按键动作与遥控器优先音频路由。自动化自测、Release 构建和自包含打包已通过；真实遥控器与干净 Windows 机器矩阵仍按清单待验证。

```powershell
./Windows/run-self-tests.ps1
./Windows/build.ps1
./Windows/package.ps1 -Runtime win-x64
```

预览包输出到 `Windows/artifacts/RemoteMic-Windows-win-x64.zip`。当前音频出口支持独立安装的 VB-CABLE；选择 `CABLE Output` 作为输入法或会议软件的麦克风。项目不会捆绑或重新分发 VB-CABLE。

当前 EXE/ZIP 尚未做 Authenticode 签名，Windows 可能显示 SmartScreen 提示；CI 仅生成 x64 预览包，ARM64 脚本路径尚未完成真机验证。

Windows 完整生产级一致性还需要两个经 Microsoft 签名的驱动：内置 `RemoteMic 2ch` 虚拟麦克风，以及只绑定 `18D1:9450` 的 HID 过滤驱动。签名驱动完成前，Raw Input 映射属于观察模式，Windows 仍可能同时执行按键原动作；项目不会要求普通用户关闭 Secure Boot 或开启测试签名。

详见 [Windows/README.md](Windows/README.md)、[Windows 驱动边界](Windows/Driver/README.md) 与 [Windows 真机验证清单](Windows/docs/VERIFY-CHECKLIST.md)。

## 已知限制

- 只按 VID/PID 匹配，不校验设备名；同 VID/PID 的其他设备也会被接管。
- helper 为 ad-hoc 签名，按代码签名 identifier 校验对端。能本机 ad-hoc 签名同 identifier 应用的攻击者可以伪装成 App，这是未做 Developer ID 签名的本机构建的固有限制。
- 本项目未做 Apple 公证；首次运行需在「系统设置 → 隐私与安全性」里放行。
- Windows 预览版尚未捆绑签名的虚拟麦克风/HID 过滤驱动，具体边界见上节。
- 原项目功能与文案版权归原作者，本 Fork 仅维护上述修复。


## 产品定位

RemoteMic 把蓝牙语音遥控器变成 macOS 上的 Vibe Coding 控制器：遥控器语音键控制豆包输入法，遥控器麦克风与 MacBook 麦克风混合为同一个 CoreAudio 输入设备 `RemoteMic 2ch`。

已接入的遥控器：

- X6-Remote（VID `0x1D5A` / PID `0xC081`）
- Google Chromecast Remote（VID `0x18D1` / PID `0x9450`）

Chromecast Remote 已实现语音键双模式：短按第一次打开、第二次关闭；长按时持续录音，松手自动关闭。APP 还提供按型号保存的按键映射，可把方向、确认、返回、Home、YouTube、Netflix、信源、音量等按键改为常用 macOS 操作。

当前版本：`1.5.0`（本 Fork）

## 1.5.0 变更（本 Fork）

- 映射新增「输入固定文本…」：按键即粘贴预设文字，剪贴板 0.3 秒后还原。
- 映射新增「多步动作…」：按键、输入文本、打开 App、聚焦输入框、等待、等待语音结束可自由组合；单步即组合键。
- 「录制任意按键」合并进「多步动作」，旧配置自动迁移，已配置的动作可反复点开编辑。
- 日志 2 GB 自动轮转，保留一份上一份；「高级功能 → 日志」可手动立即轮转。

## 1.4.5 变更（本 Fork）

- 修复语音结束后虚拟设备 IOProc 不释放、macOS 一直显示麦克风被占用的问题：豆包停止时补一次空闲释放。
- README 增加遥控器版本兼容性表。

## 1.4.4 变更（本 Fork）

- helper 每次 seize 重建 `IOHIDManager`，修复 App 重启后「seize 成功但收不到按键」。
- 合成 `⌃←` / `⌃→` 补齐 Fn 位，系统「切换桌面」正常响应。
- 录制器改用 HID 级 `CGEventTap`，录制系统快捷键时不再切换桌面。
- 映射开关与映射修改热更，无需重启；新增出厂默认映射。
- 默认显示 Dock 图标（`.regular`），可在「高级功能」中关闭。

## 1.4.3 变更（本 Fork）

- GitHub 仓库改名为 `RemoteMic`，App 的 `VRReleasesURL` 与文档链接指向新仓库。
- App 打赏窗口移除 PayPal 标签，只保留微信 / 支付宝（与 README、官网一致）。

## 1.4.2 变更（本 Fork）

- 双击 / 长按都禁用时（默认状态）不再进入手势判定，也不创建长按定时器：按下即生效、松手即发单击，按键延迟降到最短；顺手修掉了「按住超过 0.60 秒反而什么都不触发」。
- 打赏收款码换成维护者本人的微信 / 支付宝，README 与官网同源，移除没有收款码的 PayPal 入口。

## 1.4.1 变更（本 Fork）

- 修复虚拟设备 IOProc 常驻导致的「麦克风一直占用」：`RemoteMic 2ch` 改为**按需启动、空闲释放**，App 开着但没人录音时不再出现在 macOS 麦克风使用指示里。
- 修复状态栏图标四角白色方块：图标源文件圆角外侧改为真正的透明像素（抗锯齿边缘取内侧颜色），菜单栏、程序坞与系统面板图标一致。

## 1.4.0 变更（本 Fork）

- MacBook 麦克风改为**默认按需采集**：启动后不再常驻占用麦克风，仅在语音触发（键盘触发键、遥控器开麦、豆包进入录音）时打开，空闲 8 秒自动关闭；声道面板显示「按需待机 / 有声音」。
- 麦克风声道新增「始终开启」开关，需要最低延迟或常驻电平时可切回旧行为；开关状态会持久化。
- 顶栏改为三段式布局，拉开品牌、页面切换与右侧状态之间的间距。
- 内置更新优先下载 Release 的 `.pkg`，没有 PKG 时退回 `.dmg`；发版统一用 `./release.sh` 同时附带两种安装包。

## 1.3.0 新增（本 Fork）

- 按键支持「手势级映射」：单击、双击、长按可以分别配置不同目标。
- 新增「打开 App…」目标，可选 `/Applications` 下的任意应用。
- 未配置双击 / 长按的按键保持零延迟直通，不引入 280 ms 单击等待，按住仍可重复触发。

## 1.2.1 变更（本 Fork）

- 音频输入改为「遥控器优先」：遥控器有音频流时只输出遥控器麦，不再与本地麦等权叠加。
- 遥控器断开或没有音频流时自动回退本地麦克风，虚拟设备不会输出静音，会议软件等其它 App 不会因此失声。
- 本地麦克风不再只认「内置麦」，按「内置麦 → 任意非虚拟输入设备」选择，并排除 RemoteMic 2ch / MiRemoteV / BlackHole / Parrot 等回环设备。
- 遥控器优先期间本地麦只保留最近 0.25 秒缓冲，回退接近实时。

## 1.2.0 修复（本 Fork）

- 修复 macOS 26 上 Chromecast 遥控器按键映射全部失效的问题：新增 root HID helper 完成 seize，App 与 helper 之间通过 XPC 通信。
- helper 缺失或 seize 失败时明确打印 `0xE00002C1` 原因并降级，不再静默失效。
- 移除 TelemetryDeck 遥测依赖；App 不再上报事件，构建完全离线。
- 新增 `install-helper.sh` / `uninstall-helper.sh`；打包脚本会把 helper 放进 `RemoteMic.app/Contents/Helpers/`。

## 1.1.1 修复

- 修复 Chromecast Voice Remote 使用一段时间后，短按偶尔只打开豆包、遥控器麦克风却未持续收音的问题。
- 修复电脑 Option 关闭豆包时，较晚到达的音频状态可能短暂重新开启遥控器音频的问题。
- 改进双遥控器同时连接时的会话隔离、开麦确认与有限重试。
- 修复 X6 第一次短按开麦可能被误判为旧音频事件的问题。
- 更新按键映射页中的 Chromecast Remote 产品图。

## 1.1.0 新功能

- 新增 Chromecast Voice Remote（VID `0x18D1` / PID `0x9450`）语音与按键支持。
- 新增 X6 Remote 与 Chromecast Voice Remote 按键映射页面。
- 豆包语音触发键可选择 Option、Command、Control、Shift 或 Fn，默认 Option；所选按键必须与豆包输入法设置一致。
- 映射设置跟随型号，不绑定某一只遥控器；更换同型号设备后继续沿用。
- 启用映射后拦截设备原始动作，再由 RemoteMic 发送所选键盘、媒体键或自定义快捷键；关闭映射即可恢复系统原行为。
- 主控制台去掉 X6 专属措辞，X6 与 Chromecast Remote 可以同时连接。

![RemoteMic 1.4.3 音频控制台](https://theshadow178.github.io/RemoteMic/assets/screenshots/remotemic-audio-1.4.3.png)

![RemoteMic 1.4.3 Chromecast 按键映射](https://theshadow178.github.io/RemoteMic/assets/screenshots/remotemic-mapping-chromecast-1.4.3.png)

完整历史见 [CHANGELOG.md](CHANGELOG.md)。

当前支持边界：Chromecast 仅确认上述 VID/PID；X6 鼠标模式切换键由设备固件内部处理，不能单独重映射。语音键保留给豆包语音控制。Fn 的实际行为取决于 Mac 与豆包配置，建议首次使用时现场测试。

## 支持开发

RemoteMic 免费提供。制作不易，麻烦打个赏补偿一点我的 Token 费吧。谢主隆恩🙏🙏🙏～～～

| 微信 / WeChat | 支付宝 / Alipay |
|---|---|
| <img src="https://theshadow178.github.io/RemoteMic/assets/donate/wechat-qr.png" alt="微信打赏二维码" width="220"> | <img src="https://theshadow178.github.io/RemoteMic/assets/donate/alipay-qr.png" alt="支付宝打赏二维码" width="220"> |

## 仓库与公开服务边界

| 内容 | 位置 | 是否公开 |
|---|---|---|
| APP、驱动构建、测试与设计 | 本 GitHub 仓库 | 是 |
| 官网静态源文件 | `docs/` | 是，部署结果见官网 |
| 官网 | 本仓库 `docs/` 静态站（GitHub Pages） | 是 |
| 更新、PKG/DMG | [github.com/TheShadow178/RemoteMic/releases](https://github.com/TheShadow178/RemoteMic/releases) | 是 |
| 原始宣传视频与市场素材 | 仓库外 `../marketing/` | 否，不上传 Git |

遥控器相关 GitHub 仓库：

- [VincentKingHsu/vRemoter](https://github.com/VincentKingHsu/vRemoter)
- [VincentKingHsu/x6-remote-voice-bridge](https://github.com/VincentKingHsu/x6-remote-voice-bridge)
- [VincentKingHsu/MiRemoteVoice](https://github.com/VincentKingHsu/MiRemoteVoice)

## 目录

- `Sources/vRemote/`：APP 主程序、BLE/ATVV、双麦混音、UI、更新与购买配置。
- `Sources/RemoteMicHIDHelper/`：本 Fork 新增的 root HID helper（按键独占与 XPC 回传）。
- `Sources/HIDHelperShared/`：APP 与 helper 共用的 XPC 协议与常量。
- `Driver/`：生成 `RemoteMicDriver.driver` 的实验性构建脚本。
- `Packaging/`：APP 元数据、本地化与 PKG 安装脚本。
- `Resources/`：APP 内置权限向导、商业入口与支持开发图片。
- `SelfTests/`：硬件协议与更新配置回归数据。
- `Design/`：冻结 UI 和 Logo 的 Figma 本地插件源稿。
- `docs/`：公开官网静态源文件。
- `Server/`：马来西亚服务器配置与发布清单模板。
- `OPERATIONS.md`：从构建到上线和回滚的操作手册。

## 构建

```bash
./run-self-tests.sh
./package-app.sh
./build-pkg.sh
./build-dmg.sh
# 构建 PKG + DMG 并直接发布到 GitHub Releases
./release.sh
```

- `PKG`：包含 APP 与音频驱动，首次安装和版本升级都用它；内置更新优先下载它。
- `DMG`：仅包含 APP，适合已经安装过驱动、只想替换应用的场景。
- `release.sh`：依次跑自测、构建 PKG 与 DMG，并用 `gh` 把两个安装包作为附件发到 `v$VERSION` 的 Release。可选参数为发布说明文件：`./release.sh notes.md`。

当前发布包尚未完成 Developer ID 签名与公证，下载页必须明确标注这一状态；正式广泛分发前仍须完成签名、公证与真实机器安装验证。

## 更新策略

APP 从 `Info.plist` 的 `VRReleasesURL` 读取版本清单，指向本 Fork 的 GitHub Releases API：

`https://api.github.com/repos/TheShadow178/RemoteMic/releases?per_page=10`

- 内置更新优先下载 Release 附件中的 `.pkg`，没有 PKG 时退回 `.dmg`。
- 因此每次发版都必须附带 PKG 或 DMG，否则“立即更新”只能打开 Release 页面。
- 首次安装同样推荐 PKG。

购买链接从以下地址动态读取：

`https://updates.vincentstudio.org/vremoter/commerce.json`

修改公开配置即可控制购买入口，不需要重新发布 APP。没有启用且有效的购买链接时，APP 会隐藏“购买遥控器”按钮。

> 本 Fork 已从 `Info.plist` 移除 `VRCommerceConfigURL`，不会再请求该地址。`VRReleasesURL` 指向本 Fork 的 GitHub Releases（`TheShadow178/RemoteMic`），检查更新与下载入口都来自该仓库；仍可通过 Info.plist 覆盖为其他清单地址。

## 发布前阻断项

1. 当前驱动是对 BlackHole GPL v3 二进制的修改构建。公开分发前必须选择：提供驱动对应源码与构建材料，或取得适用于闭源分发的商业授权。
2. APP、驱动与 PKG/DMG 尚需 Developer ID 签名和 notarization。
3. 每次发布都要验证首次 PKG 安装、DMG 覆盖更新、自动检查更新、购买链接刷新和卸载/回滚。

## 相关文档

- [完整发布与服务器运维手册](OPERATIONS.md)
- [第三方许可说明](THIRD_PARTY_NOTICES.md)
- [音频驱动说明](Driver/README.md)
- [冻结 UI 说明](Design/vRemoter-UI-v1-Frozen/UI-FREEZE.md)
