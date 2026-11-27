#!/bin/bash
# apply-patch.sh — Patch or restore Mono DLLs for HiDPI
#
# Usage:
#   apply-patch.sh apply   — Backup originals and install patched DLLs
#   apply-patch.sh restore — Restore original DLLs from backup
#   apply-patch.sh status  — Show whether the patch is applied
#
# This patches two DLLs:
#   - System.Drawing.dll      (Patch A: Font.GetHeight at 96 DPI)
#   - System.Windows.Forms.dll (Patches B-F: font/icon/RTF scaling)

set -euo pipefail

PATCHER=/usr/lib/mono-hidpi-patch/patch_swf.exe
CECIL_DIR=/usr/lib/mono/gac/Mono.Cecil/0.11.1.0__0738eb9f132ed756

SD_GAC=/usr/lib/mono/gac/System.Drawing/4.0.0.0__b03f5f7f11d50a3a
SWF_GAC=/usr/lib/mono/gac/System.Windows.Forms/4.0.0.0__b77a5c561934e089

SD_DLL="$SD_GAC/System.Drawing.dll"
SWF_DLL="$SWF_GAC/System.Windows.Forms.dll"
SD_BACKUP="$SD_DLL.orig"
SWF_BACKUP="$SWF_DLL.orig"

check_backup() {
    local has_sd=0 has_swf=0
    [[ -f "$SD_BACKUP" ]] && has_sd=1
    [[ -f "$SWF_BACKUP" ]] && has_swf=1
    echo $((has_sd + has_swf))
}

case "${1:-status}" in
    apply)
        if [[ -f "$SD_BACKUP" || -f "$SWF_BACKUP" ]]; then
            echo "Backups already exist. Run '$0 restore' first to re-apply."
            exit 1
        fi

        TMPDIR=$(mktemp -d)
        trap 'rm -rf "$TMPDIR"' EXIT

        echo "Backing up original DLLs..."
        cp "$SD_DLL" "$SD_BACKUP"
        cp "$SWF_DLL" "$SWF_BACKUP"

        echo "Copying originals to temp dir..."
        cp "$SD_BACKUP" "$TMPDIR/"
        cp "$SWF_BACKUP" "$TMPDIR/"

        echo "Applying HiDPI patches (A-F)..."
        MONO_PATH="$CECIL_DIR" mono "$PATCHER" "$TMPDIR" "$TMPDIR"

        echo "Installing patched DLLs..."
        cp "$TMPDIR/System.Drawing.dll" "$SD_DLL"
        cp "$TMPDIR/System.Windows.Forms.dll" "$SWF_DLL"

        echo "Done. Restart any running Mono WinForms apps."
        ;;
    restore)
        if [[ ! -f "$SD_BACKUP" && ! -f "$SWF_BACKUP" ]]; then
            echo "No backups found — nothing to restore."
            exit 1
        fi
        echo "Restoring original DLLs..."
        [[ -f "$SD_BACKUP" ]] && mv "$SD_BACKUP" "$SD_DLL" && echo "  Restored System.Drawing.dll"
        [[ -f "$SWF_BACKUP" ]] && mv "$SWF_BACKUP" "$SWF_DLL" && echo "  Restored System.Windows.Forms.dll"
        echo "Done. Restart any running Mono WinForms apps."
        ;;
    status)
        count=$(check_backup)
        if [[ $count -eq 2 ]]; then
            echo "HiDPI patch is APPLIED (both backups exist)"
        elif [[ $count -eq 1 ]]; then
            echo "HiDPI patch is PARTIAL (only one backup exists — run restore then apply)"
        else
            echo "HiDPI patch is NOT applied"
        fi
        ;;
    *)
        echo "Usage: $0 {apply|restore|status}"
        exit 1
        ;;
esac
