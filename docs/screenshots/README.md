# AI Text Improver — screenshot gallery

This directory holds the visual reference for `project_design_guide.md`
§15.3 (Phase E task 19). Captures are taken **manually** by a human
after the Phase E task 18 smoke checklist passes — automated visual
regression is tracked separately as a Phase E follow-up (§15.1).

## What to capture

| File | Content | Theme |
|---|---|---|
| `settings-general-light.png` | SettingsWindow on the General tab | Light |
| `settings-general-dark.png` | SettingsWindow on the General tab | Dark |
| `settings-prompts-light.png` | SettingsWindow on the Prompts tab, mid-edit | Light |
| `settings-prompts-dark.png` | SettingsWindow on the Prompts tab, mid-edit | Dark |
| `models-dialog-light.png` | ModelsDialog with the model list populated and one row selected | Light |
| `models-dialog-dark.png` | Same | Dark |
| `confirm-dialog-reset.png` | ConfirmDialog from Reset Settings (destructive primary button visible) | Light or Dark |
| `prompt-picker.png` | PromptPickerWindow popover with the keys list | Either |
| `toast-progress.png` | ToastWindow showing the in-flight `Progress` kind with cancel button | — |
| `toast-info.png` | ToastWindow showing the `Info` (Готово) kind | — |
| `toast-error.png` | ToastWindow showing the `Error` kind | — |
| `tray-icon.png` | Windows tray icon at default DPI, hovered | — |

## Capture conventions

- 100% DPI for the primary set; redo `settings-general-*` at 150% as
  `settings-general-light-150dpi.png` to confirm scaling.
- Cropping: include the entire window chrome plus 8 px padding on
  each side. PNG, no transparency.
- File size: keep under 200 KB each via PNG compression
  (`pngquant --quality 85-95` if available).
- Shadow: Win11 default; capture with the system shadow enabled.

## Updating

When a Phase D-or-later change visibly alters one of the windows, re-
capture the affected images in the same session as the code change
and amend the commit. The `project_design_guide.md` §0 status
markers should already reflect that the visual is current.
