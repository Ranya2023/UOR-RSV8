; UOR-RC — normal Windows installer (the portable .exe stays the default download)
#define AppName "UOR-RC"
#define AppVer  GetEnv("UORRC_VERSION")
#if AppVer == ""
  #define AppVer "1.0.0"
#endif

[Setup]
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher=University of Raparin - English Department
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=UOR-RC-Setup
SetupIconFile=..\windows\remco.ico
UninstallDisplayIcon={app}\UOR-RC.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "UOR-RC.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\UOR-RC.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\UOR-RC.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start UOR-RC when Windows starts"; GroupDescription: "Options:"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "UOR-RC"; \
  ValueData: """{app}\UOR-RC.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\UOR-RC.exe"; Description: "Open UOR-RC now"; Flags: nowait postinstall skipifsilent
