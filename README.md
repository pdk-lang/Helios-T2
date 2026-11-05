# Helios T2

**Thermal Fan Curve, Processor Speed Manager & 80% Battery Health Limiter for T2-Equipped Laptops (`2018–2020`) Running Windows 10 / 11.**

> ### Built with AI — Zero Coding Skills
> **Full transparency:** I have **no programming or coding background**. This entire program, its hardware communication with the onboard T2 System Management Controller (`\\.\APPLESMC`), the Dark Obsidian & Gold interface, and the standalone `.exe` installer were built step-by-step in collaboration with an AI coding assistant.
>
> I wanted a simple, reliable way to keep my 2020 T2 laptop cool, stop it from overheating and locking at `400 MHz` under Windows, and protect its battery health at `80%` when plugged in—and I'm sharing **Helios T2** openly so anyone else running Windows on a T2 laptop can use it too.

---

## Download & One-Click Installation

1. Go to the [**Releases**](https://github.com/pdk-lang/Helios-T2/releases/latest) page.
2. Download **`HeliosT2_Setup_v1.2.0.exe`**.
3. Run **`HeliosT2_Setup_v1.2.0.exe`** and click **`Install & Launch Helios T2`**.

The standalone installer (`~215 KB`) contains everything inside a single `.exe` file:
- Installs **Helios T2** (`MacAirControl.exe`)
- Installs and starts the **T2 SMC Kernel Driver (`applesmc.sys`)**
- Creates **Desktop & Start Menu shortcuts** (`Helios T2`)
- Registers a **Windows Scheduled Task** so Helios T2 starts automatically in the system tray on both AC and battery power without administrator popup prompts

---

## Every Setting Explained in Plain English

### 1. Overview & Dashboard (`Tab 1`)
- **Dual Live Hardware Gauges**:
  - **Processor Temperature & Clock Gauge**: Shows real-time processor core temperature (`TCXC`), live processor clock speed (`MHz / GHz`), and CPU utilization (`%`).
  - **Cooling Fan Gauge**: Shows live exhaust fan speed (`F0Ac` RPM), target fan speed (`F0Tg`), percentage load, and hardware RPM bounds (`2700–8000 RPM`).
- **60-Second Live History Graph**: Plots `CPU °C`, `Fan %`, and `Load %` over the last 60 seconds.
- **Quick System Profiles**:
  - **Silent Office (`1.1 GHz`)**: Turns off Processor Turbo Boost so the laptop runs cool, quiet, and saves battery.
  - **Balanced Daily (`2.3 GHz` — Recommended)**: Caps processor boost right at the cooling heatsink's efficiency sweet spot—fast and responsive for everyday work without overheating or `400 MHz` thermal throttling.
  - **Max Cooling (`3.5 GHz`)**: Unlocks full processor speed and ramps the cooling fan earlier (`55°C–78°C`) for heavy workloads.

### 2. Cooling & Fan Curve (`Tab 2`)
- **Smart Temperature Curve (Recommended)**: Smoothly increases fan speed from `2700 RPM` (quiet idle) at your chosen **Ramp Start Temperature (`60°C`)** up to `8000 RPM` (`100%` cooling) at your **Maximum Cooling Temperature (`85°C`)** *before* heat builds up in the chassis.
- **Default Auto**: Lets the built-in motherboard firmware control the fan (stays silent longer, but allows the processor to reach `90°C+` before spinning up).
- **Manual Fixed Speed**: Locks the cooling fan to any constant RPM speed you choose (`2700–8000 RPM`).
- **Visual Fan Curve Editor**: Plots `Processor Temperature (°C)` vs. `Fan Speed (RPM)` with a live gold indicator showing where your system is operating right now.

### 3. Processor & Turbo Boost (`Tab 3`)
- Thin laptops have a compact cooling heatsink. If the processor boosts to its full `3.5 GHz` continuously, heat builds up faster than the fan can exhaust it, causing the motherboard to emergency-throttle the processor down to `400 MHz`.
- Choose between **Eco Silent (`1.1 GHz`)**, **Balanced (`1.8 GHz`)**, **Sweet Spot (`2.3 GHz`)**, or **Max Turbo (`3.5 GHz`)** at any time.
- Includes live sensor readouts for **Processor Cores (`TC1C–TC4C`)**, **Integrated Graphics (`TCGC`)**, **Logic Board (`TW0P`)**, **Palm Rest (`Ts0P`)**, and **Battery (`TB0T`)**.

### 4. Battery Health & Startup (`Tab 4`)
- **80% Battery Health Limiter (`BCLM` / `BFCL` / `CH0B`)**: Keeping a lithium-ion battery at `100%` charge while plugged into AC power all day accelerates chemical aging and capacity loss. Enabling the **80% Charge Limit** instructs the hardware charging controller to pause charging at `80%` and run the laptop directly off the power adapter.
- **Charge to 100% Now**: One-click button to allow a full `100%` charge before traveling.
- **Critical Overheat Protection (`90°C`)**: Automatically commands `100%` fan speed if the processor ever reaches `90°C`.
- **Low-Battery Shutdown Safety**: Restores normal charging on shutdown if the battery is below `25%`, ensuring the laptop can always charge while powered off.

---

## Supported Hardware (`Windows 10 / 11`)

| Model | Identifier | Year | Notes |
| :--- | :--- | :--- | :--- |
| **13-inch T2 Air (Retina)** | `MacBookAir9,1` | 2020 (Intel Ice Lake) | Primary reference hardware (`i5-1030NG7` / `i7-1060NG7`) |
| **13-inch T2 Air (Retina)** | `MacBookAir8,1`, `8,2` | 2018 – 2019 | Full T2 MMIO SMC support |
| **13 / 15 / 16-inch T2 Pro** | `MacBookPro15,x`, `16,x` | 2018 – 2020 | Full T2 MMIO SMC support |

---

## License

Released under the [MIT License](LICENSE). Built with AI assistance by Peter Karlsson (`pdk-lang`).
