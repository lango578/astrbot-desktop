; AstrBot 桌面版 Windows 安装程序（Inno Setup 6）
;
;   tools\innosetup\ISCC.exe /DSourceDir="<构建工程目录>\dist-new\AstrBot" build\setup_astrbot.iss
;
; 装完产物在 installer\AstrBot-Setup-<版本>.exe。
;
; 语言：简体中文 / 繁體中文 / English 三套。按系统 UI 语言自动挑，挑不出来才弹语言框；
;       要强制：Setup.exe /LANG=chinesetraditional。
;       本文件必须存成「UTF-8 带 BOM」：没 BOM 时 Inno 按系统 ANSI 代码页读，中文会变乱码。
;
; 两个坑（都在这台机器上实测过）：
;   1) {cm:X} 只认语言文件里编译期就存在的消息名，脚本自己加的消息会被编译器按
;      "not recognized by this version" 忽略，运行时只剩 ?X —— 所以文案写在 [Code]
;      的 L() 里按 ActiveLanguage 现场取。
;   2) [Setup] 的 InfoBeforeFile 不支持语言后缀，所以正文在 InitializeWizard 里按
;      语言换进内置说明页（页面标题本身由语言文件给）。

#define AppName "AstrBot 桌面版"
#define AppVersion "4.29.3"
#define AppURL "https://astrbot.app"
#ifndef SourceDir
  #define SourceDir "..\dist-new\AstrBot"
#endif

[Setup]
; 固定 GUID：换版本、重打包都必须沿用，否则升级会被当成另一个程序装两遍
AppId={{8D4B62F1-7E5A-4C1B-9F3D-A6C0E2B7D41A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=AstrBot 桌面版构建
AppPublisherURL={#AppURL}
DefaultDirName={autopf}\AstrBot
DefaultGroupName=AstrBot
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\installer
OutputBaseFilename=AstrBot-Setup-{#AppVersion}
SetupIconFile=assets\astrbot.ico
UninstallDisplayIcon={app}\AstrBot.exe
UninstallDisplayName={#AppName}
; 这一页的正文会按当前语言在 [Code] 里换掉（简体中文当默认，另两份随包走）
InfoBeforeFile=INSTALL_NOTE.txt
; 启动器的命名互斥体：正在运行时安装/卸载会先提示关掉
AppMutex=AstrBotDesktopLauncher
CloseApplications=yes
RestartApplications=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

[Languages]
; 顺序就是兜底顺序：系统语言既不是 zh-TW/zh-HK 也不是英文时，落到简体中文
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; data\ 排除在外：那是使用者的数据，不是构建产物。
; 光写 "data\*" 不够——createallsubdirs 会把内容为空的目录照样建出来（实测装后
; 多出一个小 data\），所以目录本身也要点名排除。
; astrbot.lock 同理：那是核心运行时留下的文件锁，被测目录跑过验收就会带着它，
; 装到别人机器上是一颗陈旧的锁（filelock 按绝对路径算，虽然不会真的挡住启动，
; 但交付包里不该出现上一台机器的运行残留）。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "data\*,data,astrbot.lock,logs\*,logs"; \
    Flags: recursesubdirs createallsubdirs ignoreversion
; 安装前说明按语言挑一份
Source: "INSTALL_NOTE.txt"; Flags: dontcopy
Source: "INSTALL_NOTE_zh_TW.txt"; Flags: dontcopy
Source: "INSTALL_NOTE_en.txt"; Flags: dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\AstrBot.exe"; WorkingDir: "{app}"
Name: "{group}\{code:LxHowTo}"; Filename: "{app}\README.txt"
Name: "{group}\{code:LxData}"; Filename: "explorer.exe"; Parameters: """{app}\data"""
Name: "{group}\{code:LxUninstall}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\AstrBot.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{code:LxDesktopTask}"; GroupDescription: "{code:LxExtraGroup}"

[Run]
Filename: "{app}\AstrBot.exe"; Description: "{code:LxLaunchNow}"; WorkingDir: "{app}"; \
    Flags: postinstall nowait skipifsilent

[Code]
// 文案不走消息表（见文件头第 1 条），按当前语言现场取。
function L(Simplified: String; Traditional: String; English: String): String;
begin
  if ActiveLanguage = 'chinesetraditional' then
    Result := Traditional
  else if ActiveLanguage = 'english' then
    Result := English
  else
    Result := Simplified;
end;

function LxHowTo(Param: String): String;
begin
  Result := L('使用说明', '使用說明', 'Read me first');
end;

function LxData(Param: String): String;
begin
  Result := L('打开数据目录', '開啟資料夾', 'Open data folder');
end;

function LxUninstall(Param: String): String;
begin
  Result := L('卸载 {#AppName}', '移除 {#AppName}', 'Uninstall {#AppName}');
end;

function LxDesktopTask(Param: String): String;
begin
  Result := L('创建桌面快捷方式', '建立桌面捷徑', 'Create a desktop shortcut');
end;

function LxExtraGroup(Param: String): String;
begin
  Result := L('附加工具：', '其他工具：', 'Additional icons:');
end;

function LxLaunchNow(Param: String): String;
begin
  Result := L('现在就启动 {#AppName}', '立即啟動 {#AppName}', 'Launch {#AppName} now');
end;

function NoteFileName: String;
begin
  if ActiveLanguage = 'chinesetraditional' then
    Result := 'INSTALL_NOTE_zh_TW.txt'
  else if ActiveLanguage = 'english' then
    Result := 'INSTALL_NOTE_en.txt'
  else
    Result := 'INSTALL_NOTE.txt';
end;

function NoteText(Name: String): String;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  if LoadStringsFromFile(AddBackslash(ExpandConstant('{tmp}')) + Name, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
      Result := Result + Lines[I] + #13#10;
  end
  else
    Result := '(readme unavailable - see README.txt after install)';
end;

procedure InitializeWizard;
var
  Name: String;
begin
  Name := NoteFileName;
  if Name <> 'INSTALL_NOTE.txt' then
  begin
    ExtractTemporaryFile(Name);
    WizardForm.InfoBeforeMemo.Text := NoteText(Name);
    WizardForm.InfoBeforeMemo.SelStart := 0;
  end;
end;

// 卸载默认保留 data\（Inno 只会删它自己装过的文件）。想连数据一起清掉，
// 必须在这一步明确确认，不给「顺手删干净」留误会空间。
// 静默卸载一律保留：/SUPPRESSMSGBOXES 会自动按默认按钮答复，那种情况下问「要不要删」
// 等于替人做了删数据的决定。
function InteractiveUninstall: Boolean;
var
  Tail: String;
begin
  Tail := UpperCase(GetCmdTail);
  Result := (Pos('/SILENT', Tail) = 0) and (Pos('/VERYSILENT', Tail) = 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{app}\data');
    if DirExists(DataDir) and InteractiveUninstall then
    begin
      if MsgBox(
           L('要连数据目录一起删除吗？', '要一併刪除資料夾嗎？', 'Delete the data folder as well?') + #13#10 +
           L('里面是机器人配置、插件和聊天记录数据库。选「否」会原样保留，下次安装直接可用。',
             '裡面包含機器人設定、外掛與聊天紀錄資料庫。選擇「否」會原封不動保留，下次安裝可直接沿用。',
             'It holds the bot configuration, plugins and message database. Choose No to keep it.') + #13#10 +
           DataDir, mbConfirmation, MB_YESNO) = IDYES then
      begin
        if not DelTree(DataDir, True, True, True) then
          MsgBox(
            L('数据目录没能全部删除（可能还有程序占用），可以稍后手动删除：',
              '資料夾未能完全移除（可能仍有程式占用），可稍後手動刪除：',
              'Some files could not be removed; delete the folder later by hand: ') + DataDir,
            mbInformation, MB_OK);
      end;
    end;
  end;
end;
