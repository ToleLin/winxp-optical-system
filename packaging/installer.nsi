; XP 光學系統 安裝檔（NSIS 3，Unicode）
; 由 tools/package.sh 或 tools/package.ps1 呼叫：
;   makensis -INPUTCHARSET UTF8 -DVERSION=0.2.2 -DSRC=<免安裝版資料夾> -DICON=<app.ico>
;            -DUSBDK_MSI=<檔名> -DOUTFILE=<輸出檔> packaging/installer.nsi
;
; 安裝內容：程式、執行引擎、USB 驅動（UsbDk，自動安裝）、捷徑、解除安裝。
; 若安裝檔旁邊有使用者自己的 XP 安裝光碟 xp.iso，會一併複製，程式第一次開啟時直接進入自動安裝。

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 64
RequestExecutionLevel admin

!ifndef VERSION
  !error "需要 -DVERSION"
!endif
!ifndef SRC
  !error "需要 -DSRC（免安裝版資料夾）"
!endif
!ifndef USBDK_MSI
  !error "需要 -DUSBDK_MSI（drivers 資料夾中的 UsbDk 安裝檔名稱）"
!endif
!ifndef OUTFILE
  !define OUTFILE "XPOptical-Setup-${VERSION}.exe"
!endif

!define APPNAME "XP 光學系統"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\XPOptical"

Name "${APPNAME}"
OutFile "${OUTFILE}"
; 路徑只含英文：執行引擎無法處理中文路徑，且資料（XP 磁碟）跟程式放在一起方便備份
InstallDir "C:\XPOptical"
InstallDirRegKey HKLM "Software\XPOptical" "InstallDir"
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1028 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1028 "FileDescription" "${APPNAME} 安裝程式"
VIAddVersionKey /LANG=1028 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1028 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1028 "LegalCopyright" "Copyright (c) 2026 ToleLin, MIT License"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!ifdef ICON
  !define MUI_ICON "${ICON}"
  !define MUI_UNICON "${ICON}"
!endif
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "這會安裝 ${APPNAME} ${VERSION}，以及 iHR320 連到 XP 所需的 USB 驅動（UsbDk）。$\r$\n$\r$\n若要一併自動安裝 XP：請把自己的 XP 安裝光碟 ISO 檔改名為 xp.iso，放在這個安裝檔旁邊後再執行。$\r$\n$\r$\n按「下一步」繼續。"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "開啟 ${APPNAME}"
!define MUI_FINISHPAGE_RUN_FUNCTION RunAsUser
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\使用說明.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "顯示使用說明"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "TradChinese"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${APPNAME} 需要 64 位元的 Windows 10 或 11。"
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

; 安裝檔以系統管理員執行，直接 Exec 會讓主程式也變成系統管理員權限，
; Windows 就不允許從檔案總管把檔案拖進視窗；改由 explorer.exe 以目前使用者身分開啟
Function RunAsUser
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\XPOptical.exe"'
FunctionEnd

Section "主程式" SecMain
  SetOutPath "$INSTDIR"
  ; 路徑用反斜線：Windows 版 makensis 不認得「/」，Linux 版會自動把「\」轉成「/」
  File "${SRC}\XPOptical.exe"
  File "${SRC}\使用說明.txt"
  File "${SRC}\THIRD-PARTY-NOTICES.txt"
  File "${SRC}\LICENSE.txt"
  File /r "${SRC}\engine"
  File /r "${SRC}\drivers"
  File /r "${SRC}\profiles"

  ; 一般使用者帳號也要能寫入（XP 磁碟、設定、紀錄都放在程式資料夾）。S-1-5-32-545 = Users 群組
  nsExec::ExecToLog 'icacls "$INSTDIR" /grant *S-1-5-32-545:(OI)(CI)M /T /Q'
  Pop $0

  ; 使用者自己的 XP 安裝光碟
  ${If} ${FileExists} "$EXEDIR\xp.iso"
  ${AndIfNot} ${FileExists} "$INSTDIR\xp.iso"
    DetailPrint "複製 XP 安裝光碟（xp.iso）…"
    CopyFiles /SILENT "$EXEDIR\xp.iso" "$INSTDIR\xp.iso"
  ${EndIf}

  ; USB 驅動：已安裝就略過
  ReadRegStr $1 HKLM "SYSTEM\CurrentControlSet\Services\UsbDk" "ImagePath"
  ${If} $1 == ""
    DetailPrint "安裝 USB 驅動（UsbDk）…"
    ${DisableX64FSRedirection}
    ExecWait '"$SYSDIR\msiexec.exe" /i "$INSTDIR\drivers\${USBDK_MSI}" /passive /norestart' $0
    ${EnableX64FSRedirection}
    ${If} $0 <> 0
    ${AndIf} $0 <> 3010
      MessageBox MB_ICONEXCLAMATION "USB 驅動安裝未完成（代碼 $0）。$\r$\n之後可以在程式右下角按「安裝 USB 驅動」重試。" /SD IDOK
    ${EndIf}
  ${Else}
    DetailPrint "USB 驅動（UsbDk）已安裝"
  ${EndIf}

  ; 捷徑（所有使用者）
  SetShellVarContext all
  CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\XPOptical.exe"
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\XPOptical.exe"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\使用說明.lnk" "$INSTDIR\使用說明.txt"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\解除安裝.lnk" "$INSTDIR\uninstall.exe"

  ; 解除安裝資訊
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKLM "Software\XPOptical" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "ToleLin"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\XPOptical.exe"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"

  Delete "$INSTDIR\XPOptical.exe"
  Delete "$INSTDIR\使用說明.txt"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\LICENSE.txt"
  RMDir /r "$INSTDIR\engine"
  RMDir /r "$INSTDIR\drivers"
  RMDir /r "$INSTDIR\profiles"

  ; XP 環境與使用者資料：預設保留（靜默解除安裝也保留），重新安裝後可繼續使用
  MessageBox MB_YESNO|MB_ICONQUESTION "要一併刪除 XP 環境嗎？$\r$\n（vm 資料夾：XP 與裡面安裝的光學軟體、資料，以及 xp.iso、設定與紀錄）$\r$\n$\r$\n選「否」會保留，重新安裝後可以直接繼續使用。" /SD IDNO IDNO keep
    RMDir /r "$INSTDIR\vm"
    RMDir /r "$INSTDIR\logs"
    Delete "$INSTDIR\xp.iso"
    Delete "$INSTDIR\settings.json"
  keep:

  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegKey HKLM "Software\XPOptical"
SectionEnd
