; ArchiveFixer 安装包（NSIS 3.x）
;
; 设计要点（改之前先读这几条，都是踩过的坑）：
;   1. ⛔ 默认安装目录：**第一个非系统固定盘上的 <盘>:\ArchiveFixer**（用户 2026-10-04 明确要求
;      "这类重要的记忆不要放 C 盘"）。只有本机确实没有第二个固定盘时才退到
;      %LOCALAPPDATA%\Programs\ArchiveFixer，并且欢迎页会**明说**"本机没有其它盘、数据会落在 C 盘"。
;      ⛔ 不许写死某个盘符（D: 不是人人都有）；⛔ 也绝不用 C:\Program Files\ ——
;      程序把日志 / 设置 / 密码列表写在 <程序目录>\data\ 下（PathService.DefaultDataRootDirectory
;      跟着 AppContext.BaseDirectory 走），Program Files 下非管理员写不进去。
;   2. RequestExecutionLevel user：免 UAC，朋友双击就能装完（每用户安装，注册表只写 HKCU）。
;   3. 静默安装 /S 时不建快捷方式（自动化验收用；正常双击安装照建）；静默档也**不问**
;      升级 / 卸载那两个问题，一律按最保守的那一档（覆盖安装 + 保留 data\）。
;   4. 升级：装之前看一眼"是不是已经装过"（注册表卸载项 / 安装目录里那个 exe），
;      装过就**问一次**：① 升级覆盖（保留 data\，默认）② 先卸载旧版再装 ③ 取消。
;      ⛔ 绝不静默删用户数据；⛔ 卸载旧版那一路走 `Uninstall.exe /S`（静默卸载 = 保留 data\）。
;   5. 卸载：有 Uninstall.exe + 控制面板「应用和功能」一项 + 开始菜单一项；
;      卸载时**问一次**（自带一页，默认 = 保留）：保留密码本与设置（data\）还是全部删除，
;      并在界面上写清"保留了什么、在哪个目录"，最后再说一遍路径。
;   6. 只删自己装的东西：根目录按后缀删程序文件、子目录整个删，唯独跳过 data\；
;      最后 RMDir "$INSTDIR"（非递归）——目录里还有别的东西就留着，不硬删。
;
; 编译：scripts\installer.ps1（或直接 makensis /DSRCDIR=... /DAPPVERSION=... 本文件）
;
; ⚠ 本文件必须存成 **UTF-8 带 BOM**：`Unicode true` 的脚本没有 BOM 时 makensis 会直接报
;   “Bad text encoding” 并中止（实测踩到过）。用编辑器改完记得确认 BOM 还在：
;   (Get-Content -Encoding Byte -TotalCount 3 <文件>) 应为 239 187 191。
;
; ⚠ 注释行末尾不留反斜杠：NSIS 会把注释里的行尾反斜杠当成续行符，把下一条命令吞掉。

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "nsDialogs.nsh"

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
!define FALLBACK_DIR "$LOCALAPPDATA\Programs\${APP_NAME}"

Name "${APP_NAME} ${APPVERSION}"
OutFile "${OUTFILE}"
BrandingText "${APP_NAME} ${APPVERSION}（绿色工具，不写注册表项以外的系统位置）"

; 每用户安装：不弹 UAC，装到用户自己的目录里
RequestExecutionLevel user
; ⚠ 真正的默认目录由 .onInit 里的 ResolveDefaultInstallDir 算（要看本机有没有第二个固定盘）。
;   这里的 InstallDir 只是兜底字符串 + 给"浏览"按钮末段名；InstallDirRegKey 命中已装过的
;   目录时会覆盖它 ⇒ 升级不搬家。
InstallDir "${FALLBACK_DIR}"
InstallDirRegKey HKCU "${APP_KEY}" "InstallDir"

VIProductVersion "${APPVERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APP_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APPVERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APPVERSION}"
VIAddVersionKey /LANG=2052 "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "MIT License"
VIAddVersionKey /LANG=2052 "Comments" "${APP_URL}"

; ⛔ 每个字段都要有一条**英文兜底**（用户 2026-10-10：「安装器里面其他用户反应有乱码的情况」）：
;    只写 /LANG=2052 时，非中文系统（英文 / 日文 …）的资源管理器「属性 → 详细信息」在拿不到英文块时
;    会退回按系统代码页去解那几行中文 ⇒ 乱码。补一组英文让它们至少能读到一句可读的说明；
;    中文系统照旧优先取 2052（行为不变）。⛔ 两组字段名必须一一对应，别只补一半。
VIAddVersionKey /LANG=1033 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${APPVERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APPVERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "MIT License"
VIAddVersionKey /LANG=1033 "Comments" "${APP_URL}"

; ---------------------------------------------------------------- 变量
Var StartMenuFolder
Var ForceShortcuts
Var InstalledDir
Var InstalledVersion
Var UpgradeChoice
Var UpgradeAlreadyHandled
Var UpgradeDialog
Var UpgradeLabel
Var UpgradeRadioOverwrite
Var UpgradeRadioUninstall
Var UpgradeRadioCancel
Var DefaultOnSystemDrive
Var UnDataChoice
Var UnDataDialog
Var UnDataLabel
Var UnDataRadioKeep
Var UnDataRadioDelete

; ---------------------------------------------------------------- 界面
!define MUI_ICON "${ICONFILE}"
!define MUI_UNICON "${ICONFILE}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "${APP_NAME} ${APPVERSION} 安装向导"
!define MUI_WELCOMEPAGE_TEXT "这一版把「批量识别 + 解压 + 整理」这套工具装到你指定的目录。$\r$\n$\r$\n· 程序自带 .NET 运行时，目标机器什么都不用装；$\r$\n· 日志、设置、密码列表都写在安装目录下的 data\ 里，那是你要长期留着的东西 —— 所以默认装到本机第一个非 C 盘的固定盘；$\r$\n· 卸载时会问你一次「保留 data\ 还是全部删除」，默认保留。$\r$\n$\r$\n点「下一步」继续。"

; ⚠ 页面回调（WelcomePagePre / WelcomePageCustom / UpgradePage*）的函数体一律写在文件末尾：
;   `$mui.WelcomePage.*` 这些变量由 MUI_PAGE_WELCOME 展开时才 Var 出来，
;   函数体写在它前面 makensis 会报 warning 6000「unknown variable」（实测踩到过，语句会被整句忽略）。

; 欢迎页：本机没有第二个固定盘时，标题与正文都要明说"数据会落在 C 盘"
!define MUI_PAGE_CUSTOMFUNCTION_PRE WelcomePagePre
!define MUI_PAGE_CUSTOMFUNCTION_SHOW WelcomePageCustom
!insertmacro MUI_PAGE_WELCOME

!ifdef LICENSEFILE
    !define MUI_LICENSEPAGE_TEXT_TOP "ArchiveFixer 本体是 MIT 许可；随包分发的 7-Zip（LGPL）与 UnRAR（RARLAB freeware）许可原文在安装目录的 tools\ 下。"
    !insertmacro MUI_PAGE_LICENSE "${LICENSEFILE}"
!endif

; 已装过旧版时先插一页"升级怎么走"（装没装过由 .onInit 判定，没装过就在 PRE 里 Abort 跳过）
!define MUI_PAGE_CUSTOMFUNCTION_PRE UpgradePagePre
Page custom UpgradePage UpgradePageLeave
!insertmacro MUI_PAGE_DIRECTORY
!define MUI_STARTMENUPAGE_REGISTRY_ROOT "HKCU"
!define MUI_STARTMENUPAGE_REGISTRY_KEY "${APP_KEY}"
!define MUI_STARTMENUPAGE_REGISTRY_VALUENAME "StartMenuFolder"
!define MUI_STARTMENUPAGE_DEFAULTFOLDER "${APP_NAME}"
!insertmacro MUI_PAGE_STARTMENU Application $StartMenuFolder
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APP_NAME}"
!define MUI_FINISHPAGE_NOREBOOTSUPPORT
!insertmacro MUI_PAGE_FINISH

; ---------------------------------------------------------------- 卸载侧：自带一页"data\ 怎么办"
; ⚠ 自定义页要用 PageEx 自己声明（MUI2 只给"确认页 / 进度页"那两张现成的 un 页宏）；
;   这里的写法照抄 MUI2 自己的 Welcome.nsh：PageEx un.custom + MUI_UNPAGE_INIT。
; ⚠ `PageEx <type>` 的 PageCallbacks 只认两档：`creator leave` 或 `pre show leave`
;   —— 给 un.custom 塞三个回调 makensis 会当场报 "Usage: PageCallbacks"（实测踩到过）。
;   静默档要跳过这一页只能写在 creator 里（IfSilent Abort），不能用 pre 回调。
!macro MUI_UNPAGE_DATA_CHOICE
    !insertmacro MUI_UNPAGE_INIT

    Function un.DataChoiceShow_${MUI_UNIQUEID}
        ; 静默卸载不问（$UnDataChoice 在 un.onInit 里已经置成 keep）
        IfSilent un_data_show_done
        !insertmacro MUI_HEADER_TEXT "卸载 ${APP_NAME}" "最后一步：data\ 怎么办"
        nsDialogs::Create 1018
        Pop $UnDataDialog
        StrCmp $UnDataDialog "error" un_data_show_done

        ${NSD_CreateLabel} 0 0 100% 46u "程序会被卸掉，但下面这个目录里放的是你长期要留着的东西，不是程序：$\r$\n$INSTDIR\data\$\r$\n· password-list.dat —— 密码本（按本机加密）$\r$\n· appsettings.json —— 你调过的设置与习惯$\r$\n· logs\ —— 历史日志"
        Pop $UnDataLabel

        ${NSD_CreateRadioButton} 0 56u 100% 24u "保留密码本与设置（推荐）：只删程序，上面那个 data\ 原样留着；以后重装能接着用"
        Pop $UnDataRadioKeep
        ${NSD_CreateRadioButton} 0 84u 100% 24u "全部删除：连 data\ 一起删（密码本、设置、日志全没，不可恢复）"
        Pop $UnDataRadioDelete

        SendMessage $UnDataRadioKeep ${BM_SETCHECK} ${BST_CHECKED} 0
        ${NSD_SetFocus} $UnDataRadioKeep

        nsDialogs::Show
        un_data_show_done:
    FunctionEnd

    Function un.DataChoiceLeave_${MUI_UNIQUEID}
        ${NSD_GetState} $UnDataRadioDelete $0
        StrCmp $0 ${BST_CHECKED} 0 un_data_leave_done
        StrCpy $UnDataChoice "delete"
        un_data_leave_done:
    FunctionEnd

    PageEx un.custom
        PageCallbacks un.DataChoiceShow_${MUI_UNIQUEID} un.DataChoiceLeave_${MUI_UNIQUEID}
    PageExEnd
!macroend

; ⚠ 卸载页与 MUI_LANGUAGE 的先后：页面按这里的顺序出现，MUI_LANGUAGE 必须排在最后
!insertmacro MUI_UNPAGE_DATA_CHOICE
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ---------------------------------------------------------------- 命令行开关
; /SHORTCUTS=1 让**静默**安装也照建快捷方式（只给自动化验收用；正常双击安装不受影响，
; 因为非静默本来就建）。验收脚本靠它走一遍"建快捷方式 → 卸载时删掉自己建的那个"这条路。
; ⚠ 判据只读 ${GetOptions} 的**返回值**，别靠 IfErrors —— 它的错误标志约定容易搞反
;   （实测把"没传开关"当成"传了"，静默装也去建快捷方式，把用户自己那个桌面快捷方式覆盖掉了）。
Function .onInit
    StrCpy $ForceShortcuts "0"
    StrCpy $UpgradeChoice "overwrite"
    StrCpy $UpgradeAlreadyHandled "0"
    StrCpy $InstalledDir ""
    StrCpy $InstalledVersion ""

    ${GetParameters} $R0
    ${GetOptions} $R0 "/SHORTCUTS=" $R1
    StrCmp $R1 "" onInit_shortcuts_done
    StrCpy $ForceShortcuts "1"
    onInit_shortcuts_done:

    ; ③ 数据落点：默认目录 = 第一个非系统固定盘上的 \ArchiveFixer
    Call ResolveDefaultInstallDir

    ; ① 升级判据：注册表 / 安装目录里那个 exe，两条都要看
    ReadRegStr $R0 HKCU "${APP_KEY}" "InstallDir"
    StrCmp $R0 "" onInit_no_reg
    StrCpy $InstalledDir $R0
    ReadRegStr $R1 HKCU "${APP_KEY}" "Version"
    StrCpy $InstalledVersion $R1
    StrCmp $InstalledVersion "" 0 +2
    StrCpy $InstalledVersion "（没记版本）"
    ; 装过就默认接着装到老地方（升级不搬家）；老目录已经不在了才用新算的默认值
    IfFileExists "$InstalledDir\*.*" 0 onInit_reg_dir_gone
    StrCpy $INSTDIR $InstalledDir
    Goto onInit_done
    onInit_reg_dir_gone:
        DeleteRegKey HKCU "${APP_KEY}"
        StrCpy $InstalledDir ""
        Goto onInit_done
    onInit_no_reg:
    IfFileExists "$INSTDIR\${APP_EXE}" 0 onInit_done
    StrCpy $InstalledDir $INSTDIR
    StrCpy $InstalledVersion "（注册表里没记）"
    onInit_done:
FunctionEnd

; ---------------------------------------------------------------- 默认目录：第一个非系统固定盘
; ⛔ 不写死盘符、也不假设备份盘一定存在：D→Z 逐个问 Windows 这是什么盘
;   （2=可移动 3=固定 4=网络 5=光驱 6=内存盘），只认 **3（固定盘）**。
;   一个都没有 ⇒ 退 %LOCALAPPDATA%\Programs\ArchiveFixer（= C 盘），并置位
;   $DefaultOnSystemDrive 让欢迎页把这件事说明白。
Function ResolveDefaultInstallDir
    StrCpy $DefaultOnSystemDrive "0"
    StrCpy $R2 "D"
    resolve_dir_loop:
        StrCmp $R2 "Z" resolve_dir_none
        System::Call 'kernel32::GetDriveType(t "$R2:\") i .R3'
        StrCmp $R3 "3" resolve_dir_fixed
        IntOp $R2 $R2 + 1
        Goto resolve_dir_loop
    resolve_dir_fixed:
        StrCpy $INSTDIR "$R2:\${APP_NAME}"
        Return
    resolve_dir_none:
        StrCpy $INSTDIR "${FALLBACK_DIR}"
        StrCpy $DefaultOnSystemDrive "1"
FunctionEnd

; ---------------------------------------------------------------- 安装
Section "主程序（必需）" SecMain
    SectionIn RO

    ; ① 升级：用户选了"先卸载旧版再装" ⇒ 在这里静默卸载旧版
    ;    （Uninstall.exe /S = 静默卸载，按设计**保留 data\**；它只删自己装的那些文件）
    ;    $UpgradeAlreadyHandled 是"从卸载里回来"的标记，防第二次重启时又问一遍。
    StrCmp $UpgradeChoice "cancel" 0 upgrade_do_uninstall
    Abort "用户取消了安装"
    upgrade_do_uninstall:
    StrCmp $UpgradeChoice "uninstall" 0 upgrade_install
    StrCmp $UpgradeAlreadyHandled "1" upgrade_install
    IfFileExists "$InstalledDir\Uninstall.exe" 0 upgrade_install
    DetailPrint "先静默卸载旧版本：$InstalledDir（保留 data\）"
    StrCpy $UpgradeAlreadyHandled "1"
    ClearErrors
    ExecWait '"$InstalledDir\Uninstall.exe" /S' $R4
    IfErrors 0 upgrade_install
        MessageBox MB_ICONEXCLAMATION "旧版本的卸载程序没能跑起来（可能正被占用）。$\r$\n可以继续装；装完记得自己去控制面板把旧版本那一项卸掉。"
    upgrade_install:

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

    ; 1) data\ 怎么办：卸载页上问过一次（默认 = 保留），静默档一律保留
    IfSilent uninst_keep_data
    StrCmp $UnDataChoice "delete" uninst_delete_data uninst_keep_data

    uninst_delete_data:
        DetailPrint "按你的选择：连 data\ 一起删除"
        RMDir /r "$INSTDIR\data"
        Goto uninst_files

    uninst_keep_data:
        DetailPrint "按你的选择：保留 data\（密码本与设置）"

    uninst_files:
    ; 2) 程序文件：根目录按后缀删（data\ 是子目录，不受影响）
    Delete "$INSTDIR\*.exe"
    Delete "$INSTDIR\*.dll"
    Delete "$INSTDIR\*.json"
    Delete "$INSTDIR\*.md"
    Delete "$INSTDIR\*.pdb"
    Delete "$INSTDIR\LICENSE"
    Delete "$INSTDIR\LICENSE.txt"

    ; 3) 子目录整个删，唯独跳过 data 目录（⚠ 这行注释末尾不许留反斜杠）
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
    MessageBox MB_OK|MB_ICONINFORMATION "设置、日志和密码列表保留在：$\r$\n$INSTDIR\data$\r$\n$\r$\n以后重装能接着用；不需要的话可以自己删掉这个目录。"
    uninst_no_data_note:
SectionEnd

Function un.onInit
    ; 默认 = 保留（与红线一致：判不出 / 静默一律落在"什么都不做"那一档）
    StrCpy $UnDataChoice "keep"
FunctionEnd

Function .onInstSuccess
    ; 装完把图标缓存刷新一下，免得快捷方式一时显示成白纸
    System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, i 0, i 0)'
FunctionEnd

; ================================================================
; 页面回调（⚠ 必须写在所有 MUI_PAGE_* / MUI_UNPAGE_* 与 MUI_LANGUAGE 之后：
;   $mui.WelcomePage.* 那些变量是 MUI_PAGE_WELCOME 展开时才 Var 出来的）
; ================================================================

; 欢迎页没有 PRE 回调也要求必须给一个（MUI2 的页面声明一律带 PRE/SHOW/LEAVE 三个槽）
Function WelcomePagePre
FunctionEnd

; 本机没有第二个固定盘时，欢迎页标题与正文都要明说"数据会落在 C 盘"。
; ⚠ 欢迎页是 MUI 的"整窗页"：正文控件是 $mui.WelcomePage.Text、标题是 $mui.WelcomePage.Title，
;   而带小标题栏的那套 $mui.Header.* 在整窗页上是隐藏的（改它等于没改）。
Function WelcomePageCustom
    StrCmp $DefaultOnSystemDrive "1" 0 welcome_custom_done
    SendMessage $mui.WelcomePage.Title ${WM_SETTEXT} 0 "STR:${APP_NAME} ${APPVERSION} 安装向导（本机没有其它盘）"
    SendMessage $mui.WelcomePage.Text ${WM_SETTEXT} 0 "STR:本机没有第二个固定盘，只能装到 C 盘。$\r$\n$\r$\n· 程序目录：$INSTDIR$\r$\n· 你的密码本、设置、日志会写在：$INSTDIR\data\ —— 它们将落在 C 盘上。$\r$\n$\r$\n如果你有别的盘（移动硬盘 / 第二块盘），点「下一步」后可以在目录页自己改到那个盘上。$\r$\n$\r$\n点「下一步」继续。"
    welcome_custom_done:
FunctionEnd

Function UpgradePagePre
    ; 静默档不问（走默认的"覆盖安装 + 保留 data\"）
    IfSilent upgrade_pre_skip
    ; 刚跑完"先卸载旧版"回来的那一趟不再问
    StrCmp $UpgradeAlreadyHandled "1" upgrade_pre_skip
    StrCmp $InstalledDir "" upgrade_pre_skip
    Abort
    upgrade_pre_skip:
FunctionEnd

Function UpgradePage
    !insertmacro MUI_HEADER_TEXT "已经装过 ${APP_NAME}" "选一种装法，然后点「下一步」"
    nsDialogs::Create 1018
    Pop $UpgradeDialog
    StrCmp $UpgradeDialog "error" upgrade_page_done

    ${NSD_CreateLabel} 0 0 100% 40u "检测到本机已经装过：$\r$\n$InstalledDir（版本：$InstalledVersion）$\r$\n$\r$\n默认就是第一项：旧的程序文件被换掉，data\ 里的密码本与设置原样保留。"
    Pop $UpgradeLabel

    ${NSD_CreateRadioButton} 0 50u 100% 14u "升级覆盖（推荐）：直接装新版本，保留密码本与设置（data\）"
    Pop $UpgradeRadioOverwrite
    ${NSD_CreateRadioButton} 0 70u 100% 14u "先卸载旧版本，再装新版本（同样保留 data\）"
    Pop $UpgradeRadioUninstall
    ${NSD_CreateRadioButton} 0 90u 100% 14u "取消安装"
    Pop $UpgradeRadioCancel

    SendMessage $UpgradeRadioOverwrite ${BM_SETCHECK} ${BST_CHECKED} 0
    ${NSD_SetFocus} $UpgradeRadioOverwrite

    nsDialogs::Show
    upgrade_page_done:
FunctionEnd

Function UpgradePageLeave
    ${NSD_GetState} $UpgradeRadioUninstall $0
    StrCmp $0 ${BST_CHECKED} 0 upgrade_leave_cancel
    StrCpy $UpgradeChoice "uninstall"
    Goto upgrade_leave_done
    upgrade_leave_cancel:
    ${NSD_GetState} $UpgradeRadioCancel $0
    StrCmp $0 ${BST_CHECKED} 0 upgrade_leave_done
    StrCpy $UpgradeChoice "cancel"
    upgrade_leave_done:
FunctionEnd
