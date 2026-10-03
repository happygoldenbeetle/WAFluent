; WAFluent's installer (Inno Setup 6). It installs for the current user, no admin prompt:
; the app into %LOCALAPPDATA%\Programs\WAFluent, a Start menu shortcut, a desktop one if ticked,
; and an entry in Windows' Installed apps to remove it. Chats and the link to the phone live in
; %LOCALAPPDATA%\WAFluent and are left alone, by installing over an older version and by uninstalling.
;
;   cd src
;   dotnet build -c Release -p:Platform=x64 --self-contained true -p:Version=0.1.0
;   ISCC.exe ..\tools\installer.iss            (add /DSource=<app folder> /DOutput=<folder> to point elsewhere)
;
; The build's output folder is what ships, with .NET and the Windows App SDK in it. `dotnet publish`
; isn't used: its folder lacks the app's compiled pages (WAFluent.pri, *.xbf) and crashes on start.

#define Version "0.1.0"
#ifndef Source
  #define Source "..\src\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64"
#endif
; /DSuffix=-name adds to the file name (a second build beside the first).
#ifndef Suffix
  #define Suffix ""
#endif
#ifndef Output
  #define Output "..\dist"
#endif

[Setup]
AppId={{8E5B1C8A-5E0F-4B7E-9C56-0A1F3B6D2E41}
AppName=WAFluent
AppVersion={#Version}
AppVerName=WAFluent {#Version}
AppPublisher=WAFluent
AppPublisherURL=https://github.com/happygoldenbeetle/WAFluent
AppSupportURL=https://github.com/happygoldenbeetle/WAFluent/issues
VersionInfoVersion={#Version}
DefaultDirName={localappdata}\Programs\WAFluent
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#Output}
OutputBaseFilename=WAFluent-{#Version}-setup{#Suffix}
SetupIconFile=..\src\Assets\AppIcon.ico
UninstallDisplayIcon={app}\WAFluent.exe
UninstallDisplayName=WAFluent
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Excludes: "*.pdb,\publish\*"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\WAFluent"; Filename: "{app}\WAFluent.exe"
Name: "{autodesktop}\WAFluent"; Filename: "{app}\WAFluent.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WAFluent.exe"; Description: "Open WAFluent"; Flags: nowait postinstall skipifsilent
