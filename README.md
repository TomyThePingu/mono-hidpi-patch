# mono-hidpi-patch

Fix Mono WinForms HiDPI scaling on Linux.

Mono's `libgdiplus` renders text at 96 DPI regardless of `Xft.dpi`, but `Font.Height` and `Graphics.DpiX` **do** respect it. This mismatch means `AutoScaleMode.Font` never triggers on HiDPI displays, leaving forms tiny with overlapping controls.

This patcher uses [Mono.Cecil](https://github.com/jbevain/cecil) to fix 6 methods across two Mono DLLs, making all WinForms apps (KeePass, etc.) scale correctly.

## What it fixes

| Patch | DLL | Method | Effect |
|-------|-----|--------|--------|
| A | System.Drawing | `Font.GetHeight()` | Returns 96-DPI height so layout matches actual text rendering |
| B | System.Windows.Forms | `ContainerControl.PerformAutoScale` | Scales form font by DPI/96, triggering the AutoScale cascade |
| C | System.Windows.Forms | `ToolStripManager.DefaultFont` | DPI-scaled font for menus, toolbars, status bars |
| D | System.Windows.Forms | `ToolStrip.ImageScalingSize` | Scales default 16×16 icons proportionally to DPI |
| E | System.Windows.Forms | `Control.DefaultFont` | DPI-scaled default font for all controls |
| F | System.Windows.Forms | `RichTextBox.HandleControl` | Scales RTF `\fs` font sizes by DPI/96 at parse time |

All patches are **cosmetic only** (font sizing, layout, icon scaling). They do not modify cryptographic operations, authentication, file I/O, clipboard handling, or any security-sensitive code path.

Patches only activate on Unix when DPI > ~106 (factor > 1.1), so they are no-ops on standard 96 DPI displays.

## Screenshots

| Before | After |
|--------|-------|
| Tiny unreadable text, overlapping controls | Properly scaled UI matching display DPI |

*(Add your own screenshots here)*

## Installation (Arch Linux)

### From AUR

```bash
paru -S mono-hidpi-patch
```

### Manual build

```bash
git clone https://github.com/tomcholin/mono-hidpi-patch.git
cd mono-hidpi-patch
makepkg -si
```

## Usage

### Option 1: System-wide (patches in place)

```bash
# Apply patches to /usr/lib/mono/gac/
sudo /usr/lib/mono-hidpi-patch/apply-patch.sh apply

# Check status
sudo /usr/lib/mono-hidpi-patch/apply-patch.sh status

# Restore originals
sudo /usr/lib/mono-hidpi-patch/apply-patch.sh restore
```

> **Note:** You need to re-apply after Mono package updates.

### Option 2: User-local (recommended)

Patch into a local GAC prefix — doesn't touch system files:

```bash
# Create local GAC structure
mkdir -p ~/.local/lib/mono/gac/System.Drawing/4.0.0.0__b03f5f7f11d50a3a
mkdir -p ~/.local/lib/mono/gac/System.Windows.Forms/4.0.0.0__b77a5c561934e089

# Copy originals
cp /usr/lib/mono/gac/System.Drawing/4.0.0.0__b03f5f7f11d50a3a/System.Drawing.dll \
   ~/.local/lib/mono/gac/System.Drawing/4.0.0.0__b03f5f7f11d50a3a/
cp /usr/lib/mono/gac/System.Windows.Forms/4.0.0.0__b77a5c561934e089/System.Windows.Forms.dll \
   ~/.local/lib/mono/gac/System.Windows.Forms/4.0.0.0__b77a5c561934e089/

# Patch them
mono /usr/lib/mono-hidpi-patch/patch_swf.exe \
    /usr/lib/mono/gac/ \
    ~/.local/lib/mono/gac/
```

Wait — the patcher expects flat directories. Here's the correct way:

```bash
# Create dirs
mkdir -p ~/.local/lib/mono/gac/System.Drawing/4.0.0.0__b03f5f7f11d50a3a
mkdir -p ~/.local/lib/mono/gac/System.Windows.Forms/4.0.0.0__b77a5c561934e089
mkdir -p /tmp/mono-orig /tmp/mono-patched

# Copy originals to a flat temp dir
cp /usr/lib/mono/gac/System.Drawing/4.0.0.0__b03f5f7f11d50a3a/System.Drawing.dll /tmp/mono-orig/
cp /usr/lib/mono/gac/System.Windows.Forms/4.0.0.0__b77a5c561934e089/System.Windows.Forms.dll /tmp/mono-orig/

# Patch
mono /usr/lib/mono-hidpi-patch/patch_swf.exe /tmp/mono-orig /tmp/mono-patched

# Install
cp /tmp/mono-patched/System.Drawing.dll \
   ~/.local/lib/mono/gac/System.Drawing/4.0.0.0__b03f5f7f11d50a3a/
cp /tmp/mono-patched/System.Windows.Forms.dll \
   ~/.local/lib/mono/gac/System.Windows.Forms/4.0.0.0__b77a5c561934e089/

rm -r /tmp/mono-orig /tmp/mono-patched
```

Then launch apps with the local GAC:

```bash
MONO_GAC_PREFIX=~/.local mono /path/to/app.exe
```

### KeePass wrapper

The package installs `keepass-hidpi` which sets `Xft.dpi`, `MONO_GAC_PREFIX`, and `LD_LIBRARY_PATH`:

```bash
keepass-hidpi
```

Set your DPI with `KEEPASS_DPI` (default: 216 = 96 × 2.25):

```bash
KEEPASS_DPI=192 keepass-hidpi   # for 2× scale
```

## How it works

### The root cause

On HiDPI Linux (especially Wayland compositors like niri + xwayland-satellite):

1. `Xft.dpi` is set to e.g. 216 (96 × 2.25 scale factor)
2. Mono's `Graphics.DpiX`/`DpiY` correctly returns 216
3. `Font.GetHeight()` uses this DPI → returns heights 2.25× larger than actual rendered text
4. But `libgdiplus` always renders glyphs at 96 DPI internally
5. `AutoScaleMode.Font` compares `MeasureText` (≈ design-time) → scale factor ≈ 1 → no scaling
6. Result: tiny text with overlapping layout based on oversized Font.Height

### The fix

- **Patch A** makes `Font.GetHeight()` return 96-DPI-based values, so layout calculations match actual rendered text size
- **Patches B, C, E** scale font sizes by `DPI/96` so text renders at the correct physical size
- **Patch D** scales toolbar icons proportionally
- **Patch F** scales RTF font sizes in RichTextBox

## Known limitations

- **Banner bitmap text** (e.g. KeePass startup banner showing database path) is still small. The font calculation divides by `Graphics.DpiY` from a Bitmap (216), but libgdiplus renders at 96 DPI. A fix was prototyped but caused regressions in menu rendering — deferred to a future release.
- Patches need re-applying after Mono package updates (when using system-wide mode).

## Requirements

- Arch Linux (or any distro with Mono ≥ 6.12)
- `mono` with `Mono.Cecil` in GAC
- For KeePass: `keepass`, `xorg-xrdb`

## Security

These patches are **cosmetic only**. They modify font sizing, layout calculations, and icon scaling in the Mono WinForms rendering pipeline. They do **not** touch:

- Cryptographic operations or key derivation
- Authentication or password handling
- File I/O or database operations
- Clipboard or AutoType
- Network operations
- Memory management of secrets

The patcher source (`patch_swf.cs`) is fully auditable — it uses Mono.Cecil to rewrite specific IL methods.

## License

MIT — see [LICENSE](LICENSE).
