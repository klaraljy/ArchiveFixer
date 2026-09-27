; ArchiveFixer 安装包（NSIS 3.x）
;
; 设计要点（改之前先读这几条，都是踩过的坑）：
;   1. ⛔ 默认装到 %LOCALAPPDATA%\ArchiveFixer，**绝不能默认 Program Files** ——
;      程序把日志 / 设置 / 密码列表写在 <程序目录>\data\ 下（PathService.DefaultDataRootDirectory
;      跟着 AppContext.BaseDirectory 走），Program Files 下非管理员写不进去。
;   2. RequestExecutionLevel user：免 UAC，朋友双击就能装完（每用户安装，注册表只写 HKCU）。
;   3. 静默安装 /S 时不建快捷方式（自动化验收用；正常双击安装照建）。
;   4. 卸载**默认保留 data\**（用户的日志、设置、密码列表）；只有交互式卸载并明确选「是」才删。
;      静默卸载一律保留。⛔ 不许改成"卸载就清空"。
;   5. 只删自己装的东西：根目录按后缀删程序文件、子目录整个删，唯独跳过 data\；
;      最后 RMDir "$INSTDIR"（非递归）——目录里还有别的东西就留着，不硬删。
;
; 编译：scripts\installer.ps1（或直接 makensis /DSRCDIR=... /DAPPVERSION=... 本文件）
;
; ⚠ 本文件必须存成 **UTF-8 带 BOM**：`Unicode true` 的脚本没有 BOM 时 makensis 会直接报
;   “Bad text encoding” 并中止（实测踩到过）。用编辑器改完记得确认 BOM 还在：
;   (Get-Content -Encoding Byte -TotalCount 3 <文件>) 应为 239 187 191。

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include "MUI2.nsh"
!include "FileFunc.nsh"

; ---------------------------------------------------------------- 可变参数（由命令行传入）
!ifndef APPVERSION
    !define APPVERSION "0.1.0"
!endif
!ifndef SRCDIR
    !error "必须用 /DSRCDIR=<发布目录> 指定要打包的内容"
!endif
!ifndef OUTFILE
    !define OUTFILE "ArchiveFixer-${APPVERSION}-setup.exe"
!endif
!ifndef ICONFILE
    !define ICONFILE "..\src\ArchiveFixer\Assets\ArchiveFixer.ico"
!endif

!define APP_NAME "ArchiveFixer"
!define APP_EXE "ArchiveFixer.exe"
!define APP_PUBLISHER "klaraljy"
!define APP_URL "https://github.com/klaraljy/ArchiveFixer"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ArchiveFixer"
!define APP_KEY "Software\ArchiveFixer"

Name "${APP_NAME} ${APPVERSION}"
OutFile "${OUTFILE}"
BrandingText "${APP_NAME} ${APPVERSION}（绿色工具，不写注册表项以外的系统位置）"

; 每用户安装：不弹 UAC，装到用户自己的目录里
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\${APP_NAME}"
InstallDirRegKey HKCU "${APP_KEY}" "InstallDir"

VIProductVersion "${APPVERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APP_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APPVERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APPVERSION}"
VIAddVersionKey /LANG=2052 "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "MIT License"
VIAddVersionKey /LANG=2052 "Comments" "${APP_URL}"

; ---------------------------------------------------------------- 界面
!define MUI_ICON "${ICONFILE}"
!define MUI_UNICON "${ICONFILE}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "${APP_NAME} ${APPVERSION} 安装向导"
!define MUI_WELCOMEPAGE_TEXT "这一版把「批量识别 + 解压 + 整理」这套工具装到你指定的目录。$\r$\n$\r$\n· 程序自带 .NET 运行时，目标机器什么都不用装；$\r$\n· 默认装到 %LOCALAPPDATA%\${APP_NAME}（不用管理员权限），也可以自己改目录；$\r$\n· 日志、设置、密码列表写在安装目录下的 data\ 里 —— 卸载时默认保留，会问你一次。$\r$\n$\r$\n点「下一步」继续。"

!insertmacro MUI_PAGE_WELCOME
!ifdef LICENSEFILE
    !define MUI_LICENSEPAGE_TEXT_TOP "ArchiveFixer 本体是 MIT 许可；随包分发的 7-Zip（LGPL）与 UnRAR（RARLAB freeware）许可原文在安装目录的 tools\ 下。"
    !insertmacro MUI_PAGE_LICENSE "${LICENSEFILE}"
!endif
!insertmacro MUI_PAGE_DIRECTORY
!define MUI_STARTMENUPAGE_REGISTRY_ROOT "HKCU"
!define MUI_STARTMENUPAGE_REGISTRY_KEY "${APP_KEY}"
!define MUI_STARTMENUPAGE_REGISTRY_VALUENAME "StartMenuFolder"
!define MUI_STARTMENUPAGE_DEFAULTFOLDER "${APP_NAME}"
Var StartMenuFolder
Var ForceShortcuts
!insertmacro MUI_PAGE_STARTMENU Application $StartMenuFolder
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APP_NAME}"
!define MUI_FINISHPAGE_NOREBOOTSUPPORT
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ---------------------------------------------------------------- 命令行开关
; /SHORTCUTS=1 让**静默**安装也照建快捷方式（只给自动化验收用；正常双击安装不受影响，
; 因为非静默本来就建）。验收脚本靠它走一遍"建快捷方式 → 卸载时删掉自己建的那个"这条路。
; ⚠ 判据只读 ${GetOptions} 的**返回值**，别靠 IfErrors —— 它的错误标志约定容易搞反
;   （实测把"没传开关"当成"传了"，静默装也去建快捷方式，把用户自己那个桌面快捷方式覆盖掉了）。
Function .onInit
    StrCpy $ForceShortcuts "0"
    ${GetParameters} $R0
    ${GetOptions} $R0 "/SHORTCUTS=" $R1
    StrCmp $R1 "" onInit_done
    StrCpy $ForceShortcuts "1"
    onInit_done:
FunctionEnd

; ---------------------------------------------------------------- 安装
Section "主程序（必需）" SecMain
    SectionIn RO
    SetOutPath "$INSTDIR"
    SetOverwrite on

    ; 发布目录里的东西整份拷进来（data\ 不在发布目录里，绝不会被覆盖）
    File /r "${SRCDIR}\*.*"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKCU "${APP_KEY}" "InstallDir" "$INSTDIR"
    WriteRegStr HKCU "${APP_KEY}" "Version" "${APPVERSION}"

    ; 控制面板「应用和功能」里的那一项（每用户）
    WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME} ${APPVERSION}"
    WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APPVERSION}"
    WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
    WriteRegStr HKCU "${UNINST_KEY}" "URLInfoAbout" "${APP_URL}"
    WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
    WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
    IntFmt $0 "0x%08X" $0
    WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "开始菜单快捷方式" SecStartMenu
    StrCmp $ForceShortcuts "1" SecStartMenu_make
    IfSilent SecStartMenu_done

    SecStartMenu_make:
    !insertmacro MUI_STARTMENU_WRITE_BEGIN Application
        CreateDirectory "$SMPROGRAMS\$StartMenuFolder"
        CreateShortCut "$SMPROGRAMS\$StartMenuFolder\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
        CreateShortCut "$SMPROGRAMS\$StartMenuFolder\使用说明.lnk" "$INSTDIR\docs\使用说明.md" "" "$INSTDIR\${APP_EXE}" 0
        CreateShortCut "$SMPROGRAMS\$StartMenuFolder\卸载 ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
    !insertmacro MUI_STARTMENU_WRITE_END
    WriteRegDWORD HKCU "${APP_KEY}" "StartMenuShortcuts" 1

    SecStartMenu_done:
SectionEnd

Section "桌面快捷方式" SecDesktop
    StrCmp $ForceShortcuts "1" SecDesktop_make
    IfSilent SecDesktop_done

    SecDesktop_make:
    CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
    ; ⚠ 记一笔"这个快捷方式是装的"：卸载时只有见到这个标记才敢删桌面上那个 .lnk。
    ;   没记的话，用户自己（或别的目录版本）建的 ArchiveFixer.lnk 会被误删 —— 实测踩到过。
    WriteRegDWORD HKCU "${APP_KEY}" "DesktopShortcut" 1

    SecDesktop_done:
SectionEnd

; ---------------------------------------------------------------- 卸载
Section "Uninstall"
    ; 0) 先看装的时候记了哪些东西是"我们建的"（读在删注册表之前）
    ReadRegDWORD $R2 HKCU "${APP_KEY}" "DesktopShortcut"
    ReadRegDWORD $R3 HKCU "${APP_KEY}" "StartMenuShortcuts"

    ; 1) 先问 data\ 怎么办（静默卸载一律保留）
    IfSilent keep_data
    MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION \
        "是否同时删除设置、日志和密码列表（$INSTDIR\data）？$\r$\n$\r$\n选「否」= 保留（以后重装还能接着用）；选「是」= 一并删除，不可恢复。" \
        IDYES delete_data IDNO keep_data

    delete_data:
        RMDir /r "$INSTDIR\data"

    keep_data:
    ; 2) 程序文件：根目录按后缀删（data\ 是子目录，不受影响）
    Delete "$INSTDIR\*.exe"
    Delete "$INSTDIR\*.dll"
    Delete "$INSTDIR\*.json"
    Delete "$INSTDIR\*.md"
    Delete "$INSTDIR\*.pdb"
    Delete "$INSTDIR\LICENSE"
    Delete "$INSTDIR\LICENSE.txt"

    ; 3) 子目录整个删，唯独跳过 data 目录（⚠ 这行注释末尾不许留反斜杠：NSIS 会把注释里的
    ;    行尾反斜杠当成续行符，把下一条命令吞掉 —— 实测踩到过，FindFirst 那一行整个消失了）
    FindFirst $R0 $R1 "$INSTDIR\*.*"
    uninst_loop:
        StrCmp $R1 "" uninst_loop_done
        StrCmp $R1 "." uninst_next
        StrCmp $R1 ".." uninst_next
        StrCmp $R1 "data" uninst_next
        IfFileExists "$INSTDIR\$R1\*.*" 0 uninst_next
        RMDir /r "$INSTDIR\$R1"
    uninst_next:
        FindNext $R0 $R1
        Goto uninst_loop
    uninst_loop_done:
        FindClose $R0

    ; 4) 快捷方式与注册表
    ;    桌面那个 .lnk 只有"装的时候确实是我们建的"（$R2 == 1）才删 —— 否则会误删用户自己的快捷方式
    StrCmp $R2 "1" 0 uninst_keep_desktop_lnk
    Delete "$DESKTOP\${APP_NAME}.lnk"
    uninst_keep_desktop_lnk:
    StrCmp $R3 "1" 0 uninst_keep_sm_lnk
    !insertmacro MUI_STARTMENU_GETFOLDER Application $StartMenuFolder
    Delete "$SMPROGRAMS\$StartMenuFolder\${APP_NAME}.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\使用说明.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\卸载 ${APP_NAME}.lnk"
    RMDir "$SMPROGRAMS\$StartMenuFolder"
    uninst_keep_sm_lnk:
    DeleteRegKey HKCU "${UNINST_KEY}"
    DeleteRegKey HKCU "${APP_KEY}"

    ; 5) 目录本身：非递归，里面还有别的东西就留着
    RMDir "$INSTDIR"
    IfSilent uninst_no_data_note
    IfFileExists "$INSTDIR\data\*.*" 0 uninst_no_data_note
    MessageBox MB_OK|MB_ICONINFORMATION "设置、日志和密码列表保留在：$\r$\n$INSTDIR\data$\r$\n$\r$\n不需要的话可以自己删掉这个目录。"
    uninst_no_data_note:
SectionEnd

Function .onInstSuccess
    ; 装完把图标缓存刷新一下，免得快捷方式一时显示成白纸
    System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, i 0, i 0)'
FunctionEnd
