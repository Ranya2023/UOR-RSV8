# UOR-RC — offline PowerPoint remote (Wi-Fi + Bluetooth)

An Android phone becomes a **touchpad + remote** for **real PowerPoint on Windows**.
PowerPoint itself plays the show, so every **animation, transition, trigger, morph and video
works exactly as on the laptop**. No internet, no uploading.

```
 Android phone (UOR-RC.apk)  ──  Wi-Fi (primary)  or  Bluetooth (backup)  ──►  UOR-RC.exe  ──►  PowerPoint
```

## Get the two files
Push this repository to GitHub → **Actions** builds everything → **Releases** has:

| File | Where |
|---|---|
| `UOR-RC.apk` | Android 8+ phone ("install unknown apps" once) |
| `UOR-RC.exe` | Windows 10/11 with PowerPoint — **portable**: one file, no install, no .NET. Its settings live in a `UOR-RC-data` folder next to it, so it runs from a USB stick too. |

## What's new
- **⏱ Time limit per quiz question** (optional: off / 10 / 20 / 30 / 60 s) — the phones and the projector count down, answers close by themselves and faster answers score more.
- **📸 Group photos**: in the camera, 📸 takes a photo, you type a caption ("Group 1"), and 📸 Group photos shows them all together on the projector — one by one or in a grid. Photos are saved in the UOR-RC folder on the Desktop.
- **🎤 Mic** in the camera (off unless you turn it on) sends your voice to the PC.
- **🔉 / 🔊** buttons for the phone's own loudness, and the phone goes almost silent by itself while its sound is going to the PC (can be switched off in ⚙ Settings).
- **Installer** as well as the portable exe, and a small "starting…" window.

- **New PC window** — a modern dashboard (Wi-Fi / Bluetooth / PowerPoint status, big PIN, hotspot with QR, present & send files with drag-and-drop, activity log), English / کوردی, dark and light. Uses the Edge engine built into Windows; the classic window is kept as a fallback.
- **New icon** for the PC and the phone.
- **🔈 Sound only on PC** (Mouse mode): the PC becomes the phone's Bluetooth speaker, so the phone's own speaker is silent. The phone must be paired with the PC in Bluetooth settings (Windows 10 2004 or newer).
- **Quiz**: the question editor no longer closes the app; ⚡ Quick question can have a few words next to each letter.

## Connecting — Wi-Fi first, Bluetooth as backup
**Wi-Fi is the default** (Settings → Connection → *Automatic*): it's several times faster
(instant slide pictures, smooth laser/spotlight/pen) and reaches across a classroom.
Bluetooth needs no network and takes over automatically if Wi-Fi drops; when Wi-Fi comes back
UOR-RC switches back without a gap.

**Wi-Fi, any of these (no internet needed):**
1. **Laptop hotspot** — in UOR-RC.exe press **Turn hotspot on** (or tick *Turn the hotspot on when
   UOR-RC opens*). Scan the QR code with the phone camera to join it.
   *Windows only allows this when the laptop has some network to share; if it says "not available", use option 2.*
2. **Phone hotspot** — turn on the phone's hotspot and connect the laptop to it. Always works offline.
3. **Same Wi-Fi/router** — works unless the network isolates devices (some campus Wi-Fi does).

Then on the phone: **Connect** → pick the PC marked 📶 → type the **4-digit PIN** shown in UOR-RC.exe (once).
The PC never accepts incoming connections (it connects out to the phone after the PIN check),
so **Windows Firewall never asks anything**.

**Bluetooth:** pair the phone with the PC once in Windows Settings → Bluetooth, then pick the PC marked 💻.
If both are available, UOR-RC learns they're the same PC and uses Wi-Fi.

## In class
| Phone | On the projector |
|---|---|
| **NEXT / ◀**, volume keys | PowerPoint's own next/previous animation or slide. ◀ on a slide whose animations start automatically now goes back correctly (fully-built previous slide, like ← on the laptop). |
| 🖱 Mouse | Laptop-style touchpad |
| 🔴 Laser | Size 8–48, 6 colours, ✍️ caption that follows the dot (web-app style) |
| 🔦 Spotlight | Size 60–400 and all 8 web-app styles: Classic, Theater, Minimal, Glass, Stage, Neon, Celebrate (confetti on/off), Colorful (cycling or fixed colour) |
| 🔍 Zoom | Web-app zoom: − / + (0.25 steps, 1–4×), Reset, drag to pan, pinch with two fingers in any pointer tool. Live — videos/animations keep playing while zoomed. |
| ✏️ Pen / 🖍 Highlight / 🧽 Eraser | Real PowerPoint ink |
| 🔢 Number | Tap to drop 1, 2, 3… in cycling colours (size 16–64); tap a number to remove it. Placed just above your finger with a live preview, like the web app's precision mode. |
| 📍 Text | Pin / Plain / Box styles, size, font colour, background (or none). Tap a label to edit/delete. Kurdish shows right-to-left. |
| 🗑 Clear | Clears ink, numbers and text on this slide |
| ⏱ Timer | Countdown **or stopwatch**; tick *Show on the projector* for the web-app corner timer with the big 59 / 3 / 2 / 1 / ⏰ flashes |
| ⬛ ⬜ ☰ 📝 | Black/white screen, jump to slide, speaker notes |

Numbers and text are saved per presentation (in `UOR-RC-data`), so they're still there next lesson.

## 🎓 Class tools
| Phone | On the projector |
|---|---|
| 📷 **Camera** (Mouse mode) | Document camera: the real camera picture, live and full-screen (turned the right way up automatically). Pinch = zoom, tap = focus, ⚡ light, ⏸ freeze (volume keys freeze too), 🔄 rotate. Laser / pen / zoom work on top. |
| 🧑‍🏫 **Whiteboard** (Mouse mode) | Blank board (white, grid, black, green). Write with pen / highlighter / numbers / text; NEXT = next page (➕ new page at the end). 💾 Save → every page as a picture in `Desktop\UOR-RC\Whiteboard …`. Pages are kept for next lesson. |
| 📑 **PDF / Word** (Mouse mode) | Pick a file on the phone — it opens full-screen on the PC and NEXT / ◀ turn pages like slides (with previews on the phone). Word files are converted with the Word installed on the PC; PowerPoint files open in PowerPoint and start. On the PC: **📑 Present a file…** in UOR-RC.exe does the same. |
| 🗳️ **Quiz** | Kahoot-style: students scan the QR, **type their name** and answer questions with **written choices**. Faster correct answers score more, and 🏆 **Scores** shows the leaderboard on the projector. Results show counts **and percentages**. Quizzes with several questions are **saved on the phone** and can be edited or deleted. ⚡ Quick question (A/B/C…) is still one tap away. The laptop hotspot allows up to 8 phones; for bigger classes use the classroom Wi-Fi. |
| 🎲 **Picker** | Random student picker with class lists (or type `1-30` for numbers), as big names **or a spinning wheel 🎡**. Optional "no repeats until everyone had a turn". |
| 🔎 **Lens** (tool) | A round live magnifier that follows your finger: size, 1.5×–4×, softer brightness and an optional dim around it. |

## Troubleshooting
- **No 📶 PC in the list** → phone and PC must be on the same hotspot/network, UOR-RC.exe open.
- **Wi-Fi keeps asking for the PIN** → check the PIN in UOR-RC.exe; some networks block device-to-device traffic, use a hotspot.
- **Bluetooth: "Could not connect"** → paired? UOR-RC.exe open? Toggle Bluetooth on both.
- **Nothing happens in PowerPoint** → don't run UOR-RC.exe "as administrator".

## Layout
```
android/   Kotlin app, no libraries   – MainActivity, TouchpadView, Link (both transports), WifiSide
windows/   .NET 8 WinForms tray app   – BtServer, WifiLink, Hotspot, PowerPointController (COM), Overlay (spotlight/zoom/…)
```
Protocol: one JSON object per line. Bluetooth UUID `7a1c2e90-5b3d-4f6a-9c1e-2d4b8f0a6e31`; Wi-Fi beacons UDP 47801, phone listens TCP 47802.
