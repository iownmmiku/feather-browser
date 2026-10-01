; 轻羽浏览器 Windows 安装程序（NSIS 3）
;
; 设计要点：
;   1. 打包的是自包含发布目录，用户不需要预装 .NET。
;   2. 本项目复用系统 WebView2 运行时，只做「检测 + 引导下载」：
;      附带经微软签名验证的引导程序，缺失时询问用户，同意再联网安装内核。
;   3. 应用数据在 %LOCALAPPDATA%\FeatherBrowser，卸载时询问是否一并删除。
;   4. 用固态 LZMA 压缩，162 MB 的目录压出来大约几十 MB。

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

; ---- 由命令行传入的宏（见 tools/build-installer.ps1）----
!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "1.0.0"
!endif
!ifndef PRODUCT_FILE_VERSION
  !define PRODUCT_FILE_VERSION "${PRODUCT_VERSION}.0"
!endif
!ifndef SOURCE_DIR
  !define SOURCE_DIR "..\src\bin\Release\net8.0-windows\win-x64\publish"
!endif
!ifndef OUT_DIR
  !define OUT_DIR "..\dist"
!endif
!ifndef WEBVIEW2_BOOTSTRAPPER
  !define WEBVIEW2_BOOTSTRAPPER "${OUT_DIR}\build-tools\MicrosoftEdgeWebview2Setup.exe"
!endif

!define PRODUCT_NAME "轻羽浏览器"
!define PRODUCT_NAME_EN "Feather Browser"
!define PRODUCT_EXE "FeatherBrowser.exe"
!define PRODUCT_PUBLISHER "iownmmiku"
!define PRODUCT_URL "https://github.com/iownmmiku/feather-browser"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\FeatherBrowser"
!define APP_KEY "Software\FeatherBrowser"

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUT_DIR}\FeatherBrowser-${PRODUCT_VERSION}-setup.exe"
InstallDir "$PROGRAMFILES64\Feather Browser"
InstallDirRegKey HKLM "${APP_KEY}" "InstallDir"
RequestExecutionLevel admin
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${PRODUCT_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright © 2026 ${PRODUCT_PUBLISHER}"

; ---------------------------------------------------------------- 界面

!define MUI_ABORTWARNING
!define MUI_ICON "..\assets\icon.ico"
!define MUI_UNICON "..\assets\icon.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动 ${PRODUCT_NAME}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------- 安装

Function .onInit
  ; 本程序是 64 位，装到 32 位系统上会直接跑不起来，先拦住
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "本程序需要 64 位 Windows。"
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Function CheckWebView2
  ; WebView2 运行时版本号写在两个位置：机器级与用户级，任一存在即可
  ClearErrors
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 == "0.0.0.0"
    StrCpy $0 ""
  ${EndIf}
  ${If} $0 == ""
    ReadRegStr $0 HKCU "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${EndIf}
  ${If} $0 == "0.0.0.0"
    StrCpy $0 ""
  ${EndIf}
  Push $0
FunctionEnd

Section "主程序" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"

  DetailPrint "正在释放程序文件…"
  File /r "${SOURCE_DIR}\*.*"

  ; 卸载程序
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 开始菜单与桌面快捷方式
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\卸载 ${PRODUCT_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}"

  ; 注册表：安装路径 + 卸载信息
  WriteRegStr HKLM "${APP_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${APP_KEY}" "Version" "${PRODUCT_VERSION}"

  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${PRODUCT_NAME} (${PRODUCT_NAME_EN})"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "URLInfoAbout" "${PRODUCT_URL}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_EXE}"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1

  ; 让「应用和功能」显示正确的大小
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "WebView2 内核运行时检测" SecWebView2
  SectionIn RO
  Call CheckWebView2
  Pop $0

  ${If} $0 != ""
    DetailPrint "已检测到 WebView2 运行时：$0"
  ${Else}
    DetailPrint "未检测到 WebView2 运行时"
    MessageBox MB_ICONEXCLAMATION|MB_YESNO \
      "本程序需要「Microsoft Edge WebView2 运行时」才能显示网页，但当前系统未安装。$\r$\n$\r$\n\
       是否现在通过微软引导程序下载并安装运行时？（需要联网）$\r$\n$\r$\n\
       选择「否」也可以先完成安装，之后自行到微软官网下载安装。" \
      /SD IDNO IDNO SkipWebView2

    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=MicrosoftEdgeWebview2Setup.exe "${WEBVIEW2_BOOTSTRAPPER}"
    DetailPrint "正在安装 WebView2 运行时（静默）…"
    ClearErrors
    ExecWait '"$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install' $2
    ${If} ${Errors}
      MessageBox MB_ICONEXCLAMATION|MB_OK \
        "无法启动 WebView2 引导程序，请稍后从微软官网手动安装运行时。" /SD IDOK
    ${Else}
      Call CheckWebView2
      Pop $0
      ${If} $0 != ""
        DetailPrint "WebView2 运行时安装完成：$0"
      ${Else}
        MessageBox MB_ICONEXCLAMATION|MB_OK \
          "未检测到 WebView2 运行时（引导程序返回码 $2）。请稍后从微软官网手动安装：$\r$\n\
           https://developer.microsoft.com/microsoft-edge/webview2/" /SD IDOK
      ${EndIf}
    ${EndIf}
    Delete "$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe"
    SetOutPath "$INSTDIR"

    SkipWebView2:
  ${EndIf}
SectionEnd

; ---------------------------------------------------------------- 卸载

Section "Uninstall"
  SetRegView 64

  ; 关掉正在运行的实例，否则文件删不干净
  ExecWait 'taskkill /F /IM ${PRODUCT_EXE} /T' $0
  Sleep 800
  ; /T 已结束轻羽的子进程；不要按 WebView2 进程名结束其它应用的内核。

  Delete "$INSTDIR\${PRODUCT_EXE}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR"

  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\卸载 ${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"

  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegKey HKLM "${APP_KEY}"

  ; 用户数据（书签、历史、缓存）默认保留，问一句再删
  MessageBox MB_ICONQUESTION|MB_YESNO \
    "是否同时删除个人数据？$\r$\n$\r$\n\
     包括书签、历史记录、设置与网页缓存（$LOCALAPPDATA\FeatherBrowser）。$\r$\n\
     选择「否」将保留，下次安装后仍可继续使用。" \
    /SD IDNO IDNO KeepUserData

  RMDir /r "$LOCALAPPDATA\FeatherBrowser"
  DetailPrint "已删除个人数据"

  KeepUserData:
SectionEnd

Function .onInstSuccess
  ; 安装完成后刷新图标缓存，避免快捷方式显示成白板
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, i 0, i 0)'
FunctionEnd
