Unicode True

!include "MUI2.nsh"

!define APP_NAME "SkeldForge"
!define APP_VERSION "1.0.12"
!define APP_PUBLISHER "NyxShade"
!define APP_EXE "SkeldForge.exe"
!define APP_ID "SkeldForge.NyxShade"
!define PUBLISH_DIR "..\artifacts\github-release\SkeldForge-v1.0.12-win-x64"
!define SAFE_INSTALL_DIR "$LOCALAPPDATA\Programs\SkeldForge"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "..\artifacts\github-release\SkeldForge-v1.0.12-Setup.exe"
InstallDir "${SAFE_INSTALL_DIR}"
InstallDirRegKey HKCU "Software\${APP_ID}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "${APP_NAME} • NyxShade"
Icon "..\SkeldForge\Assets\SkeldForge.ico"
UninstallIcon "..\SkeldForge\Assets\SkeldForge.ico"

VIProductVersion "1.0.12.0"
VIAddVersionKey /LANG=1055 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1055 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1055 "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey /LANG=1055 "FileDescription" "SkeldForge Kurulum Sihirbazı"
VIAddVersionKey /LANG=1055 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1055 "LegalCopyright" "Copyright © 2026 ${APP_PUBLISHER}"

!define MUI_ABORTWARNING
!define MUI_ICON "..\SkeldForge\Assets\SkeldForge.ico"
!define MUI_UNICON "..\SkeldForge\Assets\SkeldForge.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "SkeldForge'u şimdi çalıştır"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "Turkish"
!insertmacro MUI_LANGUAGE "English"

Section "SkeldForge" SEC_MAIN
  SectionIn RO
  ; Keep installation and recursive uninstall scoped to SkeldForge's dedicated
  ; per-user directory. This also overrides an unsafe command-line /D value or
  ; a modified legacy registry value.
  StrCpy $INSTDIR "${SAFE_INSTALL_DIR}"
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*.*"

  ; Remove names used by older installers during an in-place upgrade.
  Delete "$INSTDIR\SkeldForge-Kaldır.exe"
  Delete "$SMPROGRAMS\SkeldForge\SkeldForge'u Kaldır.lnk"
  WriteUninstaller "$INSTDIR\SkeldForge Deleter.exe"
  WriteRegStr HKCU "Software\${APP_ID}" "InstallLocation" "$INSTDIR"

  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "UninstallString" '"$INSTDIR\SkeldForge Deleter.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "QuietUninstallString" '"$INSTDIR\SkeldForge Deleter.exe" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "NoRepair" 1

  CreateDirectory "$SMPROGRAMS\SkeldForge"
  CreateShortcut "$SMPROGRAMS\SkeldForge\SkeldForge.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortcut "$SMPROGRAMS\SkeldForge\SkeldForge Deleter.lnk" "$INSTDIR\SkeldForge Deleter.exe"
  CreateShortcut "$DESKTOP\SkeldForge.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section "Uninstall"
  ; Açık SkeldForge işlemi log/ayar dosyalarını yeniden oluşturabilir ve kendi
  ; dosyalarını kilitli tutabilir. Veri silmeden önce uygulamayı kapat.
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /IM "SkeldForge.exe" /F'
  Sleep 750

  Delete "$DESKTOP\SkeldForge.lnk"
  RMDir /r "$SMPROGRAMS\SkeldForge"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"
  DeleteRegKey HKCU "Software\${APP_ID}"
  ; Never recursively delete a caller-controlled or tampered install path.
  StrCmp $INSTDIR "${SAFE_INSTALL_DIR}" 0 unsafe_install_path
  RMDir /r "${SAFE_INSTALL_DIR}"
  Goto install_cleanup_done

  unsafe_install_path:
    Delete "$INSTDIR\${APP_EXE}"
    Delete "$INSTDIR\SkeldForge Deleter.exe"

  install_cleanup_done:
  RMDir /r "$LOCALAPPDATA\SkeldForge"
  RMDir /r "$LOCALAPPDATA\SkeldForgeData"
SectionEnd

Function .onInit
  StrCpy $INSTDIR "${SAFE_INSTALL_DIR}"
FunctionEnd

Function un.onInit
  MessageBox MB_ICONEXCLAMATION|MB_YESNO|MB_DEFBUTTON2 \
    "SkeldForge kaldırıldığında bütün modpackler, mod kütüphanesi, ayarlar, loglar ve yedekler kalıcı olarak silinecek.$\r$\n$\r$\nDevam etmek istiyor musunuz?" \
    IDYES +2
  Abort
FunctionEnd
