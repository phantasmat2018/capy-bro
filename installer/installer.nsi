; CapyBro — NSIS installer
; Per-user install (no admin), MUI2 wizard, optional desktop shortcut + autostart.
; Build: makensis installer\installer.nsi (run from repo root)
;
; FZ6-F5 / L34 — Authenticode signing is OPTIONAL and lives in a
; separate post-build step.  NSIS has no built-in signtool integration,
; so after makensis produces the .exe, run:
;
;     pwsh installer\sign-installer.ps1
;
; …with either CAPYBRO_SIGN_THUMBPRINT (preferred — cert in
; CurrentUser\My, never touches disk) or CAPYBRO_SIGN_CERT_PATH +
; CAPYBRO_SIGN_CERT_PASSWORD (PFX file) set in the environment.
; Without those env vars the sign script is a documented no-op so
; local-dev builds continue to work; SmartScreen will warn users
; downloading the installer until a real cert is wired into CI.

!include "MUI2.nsh"

Name "CapyBro"
; Installer filename aligns with the user-facing brand — matches the
; renamed payload exe (CapyBro.exe) and Add/Remove Programs label.
; InstallDir intentionally KEEPS the historical "CapyBro" path so
; existing installs upgrade in-place without orphaning the
; %LOCALAPPDATA%\CapyBro\ directory or the
; HKCU\Software\CapyBro registry node.
OutFile "CapyBro-Setup-2.0.0.exe"
InstallDir "$LOCALAPPDATA\CapyBro"
InstallDirRegKey HKCU "Software\CapyBro" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

VIProductVersion "2.0.0.0"
VIAddVersionKey "ProductName" "CapyBro"
VIAddVersionKey "FileVersion" "2.0.0.0"
VIAddVersionKey "ProductVersion" "2.0.0.0"
VIAddVersionKey "FileDescription" "CapyBro Setup"
VIAddVersionKey "LegalCopyright" "(c) 2025"
VIAddVersionKey "CompanyName" "CapyBro"

; ---------- MUI configuration ----------

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_NOAUTOCLOSE

; Brand the installer + uninstaller .exe with our logomark — same
; logo.ico the application uses, so CapyBro-Setup-2.0.0.exe
; on the user's Downloads folder shows the brand mark in Explorer
; and the uninstall.exe inherits it too.
!define MUI_ICON "..\assets\logo.ico"
!define MUI_UNICON "..\assets\logo.ico"

!define MUI_FINISHPAGE_RUN "$INSTDIR\CapyBro.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch CapyBro"
!define MUI_FINISHPAGE_RUN_NOTCHECKED

; Second checkbox on the Finish page: opens https://capybro.app in
; the user's default browser via ShellExecute("open").  MUI's
; SHOWREADME hook is the documented vehicle for an arbitrary
; "visit / read" affordance — it isn't restricted to .txt files
; despite the name, and accepts http(s) URLs unchanged.  Checked
; by default (MUI's default state; the inverse flag would be
; MUI_FINISHPAGE_SHOWREADME_NOTCHECKED, deliberately omitted) so a
; fresh installer drives traffic to the product page; the user can
; uncheck before Finish if they don't want it.
!define MUI_FINISHPAGE_SHOWREADME "https://capybro.app"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Visit capybro.app"

; ---------- Pages ----------

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ---------- Sections ----------

Section "Application (required)" SecApp
    SectionIn RO

    SetOutPath "$INSTDIR"

    ; Upgrade-cleanup: a pre-rename install left an CapyBro.exe
    ; binary sitting in $INSTDIR.  Without this Delete, the directory
    ; would carry BOTH the old CapyBro.exe AND the new
    ; CapyBro.exe forever — old Start Menu / Desktop shortcuts created
    ; by previous installer runs would keep launching the stale binary
    ; (which still has the old hotkey + named mutex but the new feature
    ; set is in CapyBro.exe).  Silent: no-op on a fresh machine where
    ; the file is absent.
    Delete "$INSTDIR\CapyBro.exe"

    File "..\publish\win-x64\CapyBro.exe"
    File "..\assets\logo.ico"

    ; Start Menu shortcut — explicit IconLocation pointing at logo.ico
    ; so Win shell uses the brand mark even before the .exe is launched.
    CreateDirectory "$SMPROGRAMS\CapyBro"
    CreateShortCut "$SMPROGRAMS\CapyBro\CapyBro.lnk" \
        "$INSTDIR\CapyBro.exe" "" "$INSTDIR\logo.ico" 0

    ; Uninstaller
    WriteUninstaller "$INSTDIR\uninstall.exe"

    ; Registry: install location + Add/Remove Programs entry. DisplayIcon
    ; points at logo.ico so Settings → Apps shows our mark.
    WriteRegStr HKCU "Software\CapyBro" "InstallDir" "$INSTDIR"

    ; FZ6-F3 / M39 — defensive cleanup of the legacy Uninstall sub-key.
    ; Pre-fix the sub-key path was `\Uninstall\CapyBro` which
    ; survived the rebrand to CapyBro as an upgrade-orphan hazard: a
    ; user who installed pre-rebrand and then runs this installer would
    ; end up with BOTH `\Uninstall\CapyBro` and the new
    ; `\Uninstall\CapyBro` keys, doubling the app in Add/Remove Programs.
    ; DeleteRegKey is a no-op when the key is absent, so this is safe on
    ; fresh machines too.
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro"

    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "DisplayName" "CapyBro"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "DisplayIcon" "$INSTDIR\logo.ico"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "UninstallString" "$\"$INSTDIR\uninstall.exe$\""
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "DisplayVersion" "2.0.0"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "Publisher" "CapyBro"
    ; URLInfoAbout + HelpLink surface the project's homepage as the
    ; clickable "Support link" / "About link" rows in Settings → Apps →
    ; Installed apps → CapyBro on Win10/11, so a user who's hunting for
    ; docs or a contact channel finds capybro.app without having to
    ; re-google the product name.  Same string is referenced from C#
    ; (src/CapyBro/Services/AppInfo.cs Homepage) — NSIS can't read C#
    ; constants, so a future domain rename has to touch both files.
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "URLInfoAbout" "https://capybro.app"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "HelpLink" "https://capybro.app"
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "NoModify" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro" "NoRepair" 1
SectionEnd

Section /o "Desktop shortcut" SecDesktop
    CreateShortCut "$DESKTOP\CapyBro.lnk" \
        "$INSTDIR\CapyBro.exe" "" "$INSTDIR\logo.ico" 0
SectionEnd

Section /o "Launch with Windows" SecAutostart
    ; FZ6-F1 / C3 fix: must include `--silent` so the boot-time launch
    ; matches the app's own writer (AutostartService.cs:104). Without it,
    ; the app's `App.OnStartup` treats the launch as "user-initiated" and
    ; pops the Settings window on every Windows sign-in, defeating the
    ; whole purpose of the "Launch with Windows" checkbox.
    ;
    ; Run-key VALUE-NAME ("CapyBroV2") kept historical so existing
    ; installs upgrade in-place — `AutostartService.RepairIfStale()`
    ; reads the value-name by name, not by content, and refreshes the
    ; path verbatim on next launch.  Only the path (file name) is
    ; flipped to CapyBro.exe here.
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CapyBroV2" "$\"$INSTDIR\CapyBro.exe$\" --silent"
SectionEnd

; ---------- Component descriptions ----------

LangString DESC_SecApp ${LANG_ENGLISH} "CapyBro application files (required)."
LangString DESC_SecDesktop ${LANG_ENGLISH} "Place a shortcut on the desktop."
LangString DESC_SecAutostart ${LANG_ENGLISH} "Launch CapyBro automatically on Windows sign-in."

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} $(DESC_SecApp)
    !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(DESC_SecDesktop)
    !insertmacro MUI_DESCRIPTION_TEXT ${SecAutostart} $(DESC_SecAutostart)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- Uninstall ----------

Section "Uninstall"
    Delete "$INSTDIR\CapyBro.exe"
    ; Defensive — pre-rename installs put the binary under the old name.
    ; If the user installed pre-rebrand AND ran the uninstaller built
    ; from THIS rebrand-era source, the new Delete targets a file that
    ; isn't there.  Mirror the old name here so the orphan is cleaned up
    ; before RMDir /r tries to wipe the directory.
    Delete "$INSTDIR\CapyBro.exe"
    Delete "$INSTDIR\logo.ico"
    Delete "$INSTDIR\uninstall.exe"
    ; FZ6-F7: recursive remove so a future release that drops additional
    ; assets (crash dumps, cached catalogues, etc.) into $INSTDIR doesn't
    ; leave Add/Remove Programs unable to fully clean up. Safe because
    ; $INSTDIR is forced to $LOCALAPPDATA\CapyBro by line 12.
    RMDir /r "$INSTDIR"

    Delete "$SMPROGRAMS\CapyBro\CapyBro.lnk"
    RMDir "$SMPROGRAMS\CapyBro"
    Delete "$DESKTOP\CapyBro.lnk"

    ; Best-effort autostart removal — only if our value is still there
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CapyBroV2"
    DeleteRegKey HKCU "Software\CapyBro"
    ; FZ6-F3 / M39 — current sub-key is `\Uninstall\CapyBro`.  Also delete
    ; the legacy `\Uninstall\CapyBro` defensively, in case a user
    ; ran an installer pair where the install side migrated but a stale
    ; uninstaller.exe left over from a pre-rebrand version is what they
    ; actually launched — without this their Add/Remove Programs would
    ; keep showing a ghost entry.
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CapyBro"

    ; Note: ~/.ai_text_improver_v2_config.json is intentionally preserved.
    ; User-config policy: uninstall removes the binary, never the user's
    ; data. If they reinstall they keep their prompts, hotkeys, API key.
SectionEnd
