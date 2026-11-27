; =====================================================================================
; Apex Highlight Picker 安裝程式（Inno Setup 6）/ installer script (Inno Setup 6)
;
; 由 scripts\package.ps1 呼叫，不用手動跑。/ Built by scripts\package.ps1; you don't run it by hand.
;   ISCC.exe /DAppVersion=0.3.0 /DNumericVersion=0.3.0.0 /DSourceDir=...\out\publish\setup /DOutputDir=...\out\release installer\ApexHighlighter.iss
;
; - 預設只裝給目前使用者，不需要系統管理員（裝到 %LOCALAPPDATA%\Programs\Apex Highlight Picker）。
;   安裝時可以改選「所有使用者」，那時才會要求管理員權限。
;   Installs for the current user by default, no admin needed (%LOCALAPPDATA%\Programs\Apex Highlight Picker).
;   The first page lets you pick "all users" instead, which asks for admin rights.
; - 在開始功能表建立「Apex Highlight Picker」，所以 Windows 搜尋找得到。
;   Adds "Apex Highlight Picker" to the Start menu, so Windows Search finds it.
; - 設定（解除安裝）> 應用程式 可以解除安裝。/ Uninstall from Settings > Apps.
; - 內含 .NET 執行階段（self-contained），使用者不用另外裝 .NET。
;   Ships its own .NET runtime (self-contained), so users don't need to install .NET.
; =====================================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\publish\setup"
#endif
#ifndef OutputDir
  #define OutputDir "..\out\release"
#endif

#define AppName "Apex Highlight Picker"
#define AppExe "ApexHighlighter.App.exe"

[Setup]
; AppId 不能改，改了升級會變成裝第二份 / Never change AppId, or upgrades install a second copy
AppId={{7C3B2E4A-58D1-4F0B-9A6E-2D4C1F8B9E35}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=elilin0610
AppPublisherURL=https://github.com/elilin0610/Auto-Clip
AppSupportURL=https://github.com/elilin0610/Auto-Clip/issues
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoDescription={#AppName} Setup

; 只裝給自己（不需管理員），或在第一頁改選所有使用者
; Current user by default (no admin); the first page offers "all users" instead
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

SetupIconFile=..\src\ApexHighlighter.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=ApexHighlighter-{#AppVersion}-setup

; 升級時先關掉執行中的 App / close a running copy before upgrading
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; {autoprograms} = 開始功能表\程式集（目前使用者或所有使用者）/ Start menu\Programs (current or all users)
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Pick Apex Legends highlights from recordings"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
