# T2 Hardware Control Center

**Native Cooling Fan Curve, Processor Turbo Boost & 80% Battery Charge Limiter for T2-Equipped Laptops (`2018–2020`) Running Windows 10 / 11.**

A lightweight (`~105 KB`), zero-dependency C# / WPF application that communicates directly with the onboard **T2 System Management Controller (`\\.\APPLESMC` MMIO at `0xFE0B0000`)** to provide real-time thermal monitoring, proactive fan cooling, processor boost management, and hardware battery charge limiting.

---

## Key Features & Settings Explained

### 1. Overview & Dashboard (`Tab 1`)
- **Dual Live Hardware Gauges**:
  - **Processor Temperature & Clock Gauge**: Displays live processor core temperature (`TCXC`), real-time clock frequency (`MHz / GHz`), and CPU utilization (`%`).
  - **Cooling Fan Gauge**: Displays live exhaust fan speed (`F0Ac` RPM), target fan speed (`F0Tg`), percentage load, and hardware RPM bounds (`F0Mn`–`F0Mx`).
- **60-Second Live History Graph**: Plots `CPU °C`, `Fan %`, and `Load %` in real time.
- **Quick System Profiles**:
  - **Silent Eco (`1.1 GHz`)**: Disables Processor Turbo Boost so the laptop stays cool, silent, and maximizes battery runtime.
  - **Balanced Sweet Spot (`2.2 GHz AC / 1.8 GHz Battery` — Recommended)**: Caps boost frequency right at the heatsink's thermal efficiency sweet spot, eliminating the `400 MHz` thermal throttling lockup while keeping everyday applications fast and responsive.
  - **Maximum Performance (`3.5 GHz`)**: Unlocks full factory boost speed and ramps the cooling fan earlier (`55°C–78°C`) for demanding workloads.

### 2. Cooling & Fan Curve (`Tab 2`)
- **Smart Temperature Curve (Recommended)**: Smoothly increases fan speed from `2700 RPM` (quiet idle) at your chosen **Ramp Start Temperature (`60°C`)** up to `8000 RPM` (`100%` cooling) at your **Maximum Cooling Temperature (`85°C`)** *before* the heatsink heat-soaks.
- **Default Firmware Auto**: Uses the built-in hardware fan control (stays quiet longer, but allows the processor to reach `~90°C+` before ramping up).
- **Manual Constant Speed**: Locks the cooling fan to any fixed RPM speed between `2700 RPM` and `8000 RPM`.
- **Visual Response Curve**: Plots `Processor Temperature (°C)` vs. `Fan Speed (RPM)` with a live gold dot indicating the current operating point.

### 3. Processor & Turbo Boost (`Tab 3`)
- Direct switching between **Turbo Boost Off (`1.1 GHz Base`)**, **Balanced Sweet Spot (`2.2 GHz AC / 1.8 GHz Battery`)**, and **Full Uncapped Boost (`Up to 3.5 GHz`)**.
- **Individual Core & Graphics Temperatures**: Live silicon temperature readings for `Processor Core 1–4 (TC1C–TC4C)`, `Integrated Graphics (TCGC)`, and `System Controller (TCSA)`.

### 4. Battery Health & Startup (`Tab 4`)
- **Hardware Battery Charge Limiter (Default `80%`)**: Lithium-ion batteries degrade fastest when held at `100%` charge at warm temperatures (such as when plugged into an external monitor all day). Setting an `80%` hardware ceiling writes directly to the onboard SMC (`BCLM`, `BFCL`, `CH0B`) so charging pauses at `80%` and the laptop runs off AC adapter power.
- **Charge to 100%**: One-click button to allow a full `100%` charge before traveling.
- **Start with Windows**: Automatically starts minimized in the system tray at login on both AC and battery power via a Windows Scheduled Task (no administrator popup prompts).
- **Low-Battery Shutdown Safety**: Automatically restores normal charging on shutdown if the battery is below `25%`, ensuring the laptop can always charge while powered off.

---

## Supported Hardware (`Windows 10 / 11`)

| Model | Identifier | Year | Notes |
| :--- | :--- | :--- | :--- |
| **13-inch T2 Air (Retina)** | `MacBookAir9,1` | 2020 (Intel Ice Lake) | Primary reference hardware (`i5-1030NG7` / `i7-1060NG7`) |
| **13-inch T2 Air (Retina)** | `MacBookAir8,1`, `8,2` | 2018 – 2019 | Full T2 MMIO SMC support |
| **13 / 15 / 16-inch T2 Pro** | `MacBookPro15,x`, `16,x` | 2018 – 2020 | Full T2 MMIO SMC support |

---

## Installation (Standalone `.exe` Installer)

1. Download **`MacBookAirControlCenter_Setup_vX.Y.Z.exe`** from the [**Releases**](../../releases) page.
2. Run **`MacBookAirControlCenter_Setup_vX.Y.Z.exe`**.
3. Click **`Install & Launch Control Center`**.
