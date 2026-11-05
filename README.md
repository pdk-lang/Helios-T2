#  MacBook Air & T2 Control Center (`Gotham Gold // Afterburner Edition`)

**Native Hardware Fan Curve, Intel Turbo Boost & AlDente 80% Battery Charge Limiter for Apple T2 MacBooks running Windows 10 / 11 (Boot Camp).**

Replaced **Macs Fan Control** and **AlDente** with a single, zero-bloat (`~105 KB`) native C# / WPF application talking directly to the **Apple T2 System Management Controller (`\\.\APPLESMC` MMIO at `0xFE0B0000`)**.

---

## ✨ Key Features

### 1. ⚡ MSI Afterburner × Dark Gotham Gold Cockpit (`Tab 01`)
- **Dual Radial Hardware Gauges**:
  - **Left Gauge (`CPU CORE & CLOCK`)**: Live CPU PECI temperature (`TCXC`), real-time frequency (`MHz / GHz`), processor load (`%`), and dynamic thermal color transitions (Sovereign Gold `#E5B842` → Solar Gold `#FFC42E` → Gotham Crimson `#D63638`).
  - **Right Gauge (`EXHAUST TURBINE`)**: Live fan RPM (`F0Ac`), target RPM (`F0Tg`), percentage load, and hardware RPM bounds (`F0Mn`–`F0Mx`).
- **Live 60-Second Multi-Trace Hardware Oscilloscope**: Plots `CPU °C`, `Fan %`, and `CPU Load %` in real time.
- **1-Click Gotham Hardware Profiles**:
  - `🌙 SILENT ECO (1.1 GHz)` — Zero Turbo heat, whisper-quiet fan.
  - `⚡ SWEET SPOT (2.2 GHz AC / 1.8 GHz DC)` — Eliminates the notorious MacBook Air BD PROCHOT `400 MHz` thermal throttling lockup while keeping sustained performance high.
  - `🔥 MAX OVERDRIVE (3.5 GHz)` — Uncapped clock boost + aggressive cooling curve.

### 2. 🌀 Interactive Turbine & Smart Fan Curve (`Tab 02`)
- **Proactive Linear Temperature Curve**: Starts ramping smoothly at `60°C` (`2700 RPM`) up to `85°C` (`8000 RPM`) *before* the T2 heatsink heat-soaks.
- **Interactive Visual Response Curve**: Displays your live operating crosshair dot (`CPU °C` vs. `RPM`) in real time.
- **3 Operating Modes**: `Smart Temp Curve`, `Apple SMC Auto`, and `Manual Constant RPM`.

### 3. 🚀 Intel SpeedShift & Per-Core Die Telemetry (`Tab 03`)
- Instant switching between **Turbo Off (`1.1 GHz`)**, **Sweet Spot (`2.2 GHz AC / 1.8 GHz Battery`)**, and **Full Boost (`3.5 GHz`)**.
- **6-Sensor Silicon Die Matrix**: Individual live temperatures for `Core 1 (TC1C)`, `Core 2 (TC2C)`, `Core 3 (TC3C)`, `Core 4 (TC4C)`, `Intel Iris Plus GPU (TCGC)`, and `System Agent (TCSA)`.

### 4. 🔋 AlDente Hardware Battery Charge Limiter (`Tab 04`)
- Writes your target charge ceiling (default **`80%`**) directly to the Apple T2 SMC (`BCLM`, `BFCL`, `CH0B` charge inhibit) so your battery doesn't degrade when plugged into an external 4K monitor all day.
- **20-Cell Gotham Gold Battery Pack Bar** with target cap marker line, live cycle count (`B0CT`), and pack voltage (`B0AV`).
- **Boot Camp Shutdown Safety**: Automatically releases charge inhibit on shutdown if battery is below `25%`.

---

## 💻 Supported Apple T2 MacBooks (Windows 10 / 11 Boot Camp)

| Model | Identifier | Year | Notes |
| :--- | :--- | :--- | :--- |
| **MacBook Air (Retina, 13-inch)** | `MacBookAir9,1` | 2020 (Intel Ice Lake) | Primary reference hardware (`i5-1030NG7` / `i7-1060NG7`) |
| **MacBook Air (Retina, 13-inch)** | `MacBookAir8,1`, `8,2` | 2018 – 2019 | Full T2 MMIO SMC support |
| **MacBook Pro (13 / 15 / 16-inch)** | `MacBookPro15,x`, `16,x` | 2018 – 2020 | Full T2 MMIO SMC support |

---

## 📦 Installation (1-Click Standalone `.exe` Installer)

1. Download the latest **`MacBookAirControlCenter_Setup_vX.Y.Z.exe`** from the [**Releases**](../../releases) page.
2. Run **`MacBookAirControlCenter_Setup_vX.Y.Z.exe`** (requests Administrator privileges to install the `AppleSMC` kernel driver and register the startup task).
3. Click **`⚡ INSTALL & LAUNCH CONTROL CENTER`**.

The installer automatically:
- Installs `MacAirControl.exe`, `applesmc.sys`, and `app.ico` to `C:\Program Files\MacBookAirControlCenter`.
- Registers and starts the `AppleSMC` kernel service (`\\.\APPLESMC`).
- Creates Desktop and Start Menu shortcuts.
- Registers the `\MacBookAirControlCenter` Windows Scheduled Task so it starts minimized in your system tray on both AC and Battery power without UAC prompts.

---

## 🛠️ Building from Source (Zero External Dependencies)

Built using the standard .NET Framework 4.x C# compiler (`csc.exe`) included with every Windows 10/11 installation:

```powershell
# Build MacAirControl.exe + Standalone Setup Installer (.exe)
powershell -ExecutionPolicy Bypass -File .\Publish-Release.ps1 -BuildOnly
```
