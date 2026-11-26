# NDConnect — Windows 任务栏无线外设电量监测工具

轻量级、免驱动、零外部依赖的 Windows 系统托盘电量监测工具，用于实时显示无线鼠标、键盘、耳机、游戏手柄的电池电量与充电状态。

## 核心特性

- **一设备一托盘环形图标**：每个已连接的设备在任务栏托盘区独立显示一个透明底、无黑边与灰环填充的 32-bit ARGB 高 DPI 环形图标。
- **全屏游戏/应用免打扰**：自动检测 D3D 独占全屏与无边框全屏应用，全屏期间自动将轮询频率降为固定 **120 秒** 并暂停隐藏任务栏呼吸动效，退出全屏后即时恢复。
- **电量颜色渐变**：根据剩余电量从 **红色 (0%) → 橙红 (25%) → 黄色 (50%) → 黄绿 (75%) → 绿色 (100%)** 平滑渐变。
- **绿色呼吸充电动效**：检测到设备充电时，环形自动切换为绿色柔和呼吸光效与外圈光晕。
- **免驱动三通道直读**：
  1. **2.4GHz 接收器通道**：通过 Windows 原生 HID 接口直读接收器/加密狗（Logitech HID++ 1.0/2.0、Razer、ASUS ROG/TUF/OMNI、SteelSeries、Corsair Void/Slipstream/HS80/Virtuoso、HyperX、Audeze Maxwell、CompX Report-8 [Pulsar/ATK/VXE/G-Wolves/Hitscan/LAMZU Atlantis]、Nordic Feature 0x83 [WLmouse/LAMZU Maya X/MCHOSE A5]、EVision/Telink [VGN/ATK]、MCHOSE RealTek/G7/V9、Keychron、Rapoo 等）。
  2. **USB 线缆通道**：有线直连读取电量及充电状态（DualSense / DualShock 4、8BitDo、Xbox XInput、ASUS ROG、CompX、Razer、HID++、EVision、标准 USB HID Battery Usage）。
  3. **Windows 蓝牙通道**：原生读取 Windows 蓝牙 PnP 电量属性（基于容器与 MAC 聚合在线状态，兼容 Classic Bluetooth HFP / 鼠标 / 键盘 / 手柄）、Nintendo Switch Pro / Joy-Con 以及 BLE GATT 电池服务 (`0x180F` / `0x2A19` / `0x2A1A`)。
- **与厂商软件共存**：所有 HID 句柄均以 `FILE_SHARE_READ | FILE_SHARE_WRITE` 共享模式与短超时异步 I/O 瞬时打开并立即释放，不占用独占句柄，也不依赖 G HUB / Synapse / iCUE / MCHOSE HUB 等厂商应用。

## 编译与运行

使用 Windows 内置 `.NET Framework 4.8` 编译器即可一键构建（无需安装 Visual Studio 或 .NET SDK）：

```bat
build.bat
```

运行托盘程序：

```bat
bin\NDConnect.exe
```

命令行诊断与自检：

```bat
bin\NDConnect.exe --scan      # 扫描并打印当前所有通道读出的设备电量
bin\NDConnect.exe --diag      # 输出完整协议探测诊断日志
bin\NDConnect.exe --selftest  # 运行内置单元测试、GDI 泄漏检测与图标渲染测试
```
