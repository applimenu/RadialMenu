#if BuildArch == "arm64"
  #define CompatibleArch "arm64"
#else
  #define CompatibleArch "x64compatible"
#endif

[Setup]
AppId={{D2C308C1-26BB-46A3-A7D3-1E28787782F5}
AppName=RadialMenu
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\RadialMenu
DefaultGroupName=RadialMenu
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#CompatibleArch}
ArchitecturesInstallIn64BitMode={#CompatibleArch}
OutputBaseFilename=RadialMenu-Setup-win-{#BuildArch}
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\RadialMenu1.exe
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\RadialMenu"; Filename: "{app}\RadialMenu1.exe"

[Run]
Filename: "{app}\RadialMenu1.exe"; Description: "RadialMenu"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RadialMenu', Command) then
      if CompareText(Command, AddQuotes(ExpandConstant('{app}\RadialMenu1.exe'))) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'RadialMenu');
end;
