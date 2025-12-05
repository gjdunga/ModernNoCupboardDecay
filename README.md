# ModernNoCupboardDecay — Administrator Instructions

This guide explains how to configure, position, debug, and use every system included in MNCD 4.0.0.

---

## 🔧 Basic Admin Commands

### Show status:
/mncd


### Change config live:
/mncdset <option> <value>


Options:

| Option        | Description |
|---------------|-------------|
| `checkauth`   | true/false — require TC auth to protect entities |
| `teamaware`   | true/false — protect teammates when checkauth enabled |
| `radius`      | meters — TC protection bubble radius |
| `autodetect`  | true/false — detect wipe schedule from server tags |
| `wipemode`    | Manual, Weekly, BiWeekly, Monthly, or 5d/7d/etc |
| `wipestartnow`| Reset wipe start timestamp |

---

## 🧭 UI Positioning

### Set absolute UI anchors:
/mncdui <minX> <minY> <maxX> <maxY>


### Nudge UI (draggable feel):
/mncduiadd <dx> <dy>


### Reset UI:
/mncdresetui


---

## 🛰 TC Radius Preview (3D Hologram)

/mncdpreview
mncd.preview


Settings:

- `PreviewRequiresPermission`
- `PreviewRingDuration`
- `PreviewRingRadiusMultiplier`

---

## 🛡 Debug Overlay

/mncddebug
mncd.debug


Shows whether YOU are inside a protected radius.

Updates every 0.5 seconds.

---

## 📚 Help System
/mncdhelp
/mncdhelp ui
/mncdhelp set
/mncdhelp debug
/mncdhelp preview
/mncdhelp wipe

---

## 🌍 Language Files

Located in:

oxide/lang/en/ModernNoCupboardDecay.json
oxide/lang/es/ModernNoCupboardDecay.json
oxide/lang/ru/ModernNoCupboardDecay.json
oxide/lang/zh-CN/ModernNoCupboardDecay.json


## 🧪 Testing

Recommended procedure:

1. Place a TC  
2. Build within its radius  
3. Trigger decay (hurt building manually with admin tools)  
4. Run `/mncddebug` and move in/out of radius  
5. Run `/mncdpreview`  
6. Adjust UI live with `/mncduiadd`

---

## 📘 Support
Report issues via GitHub:
https://github.com/gjdunga/ModernNoCupboardDecay
