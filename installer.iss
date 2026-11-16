#define AppName "Bluetooth Notifier"
#define AppVersion "1.2.0"
#define AppExe "BtNotifier.exe"
#define AppUserModelId "BtNotifier.App"

[Setup]
AppId={{B7C4A2D1-5E3F-4A8B-9C61-2F0D7E91A4C3}
AppName={#AppName}
AppVersion={#AppVersion}
; The "Select Destination Location" page is always shown, so the user can choose where to install
DefaultDirName={autopf}\{#AppName}
DisableDirPage=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
; Per-user install by default (no admin prompt). Pick a writable folder, e.g. D:\Apps\Bluetooth Notifier
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=BtNotifier-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Setup/uninstaller ask the user to close the app if it is running
AppMutex=BtNotifier.Singleton
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; AppUserModelID gives the toast notifications their name and icon
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; The app adds its own "start with Windows" entry; these only clean up on uninstall
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "BtNotifier"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#AppUserModelId}"; ValueType: none; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopBtNotifier"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\BtNotifier"
