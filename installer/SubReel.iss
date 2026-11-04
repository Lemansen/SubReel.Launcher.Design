#ifndef AppSource
  #define AppSource "..\publish\SubReel-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef LauncherExe
  #define LauncherExe "SubReel.Launcher.exe"
#endif

[Setup]
AppId={{A8B2B2F8-3D64-4F27-9A87-1D70DCE5B4C1}
AppName=SubReel Launcher
AppVersion={#AppVersion}
AppPublisher=SubReel
DefaultDirName={localappdata}\Programs\SubReel
DefaultGroupName=SubReel Launcher
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir={#OutputDir}
OutputBaseFilename=SubReel-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#LauncherExe}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные параметры:"; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SubReel Launcher"; Filename: "{app}\{#LauncherExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\SubReel Launcher"; Filename: "{app}\{#LauncherExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#LauncherExe}"; Description: "Запустить SubReel Launcher"; Flags: postinstall nowait skipifsilent
