#define AppName "Codex Limits Widget"
#define AppVersion "1.1.1"

[Setup]
AppId={{CA276763-EE51-45D5-B486-E34BAD5703EF}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion=1.1.1.0
VersionInfoProductName={#AppName}
DefaultDirName={localappdata}\Programs\Codex Limits Widget
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0
AppMutex=Local\CodexLimitsWidget
UninstallDisplayIcon={app}\CodexLimitsWidget.exe
SetupIconFile=..\assets\app.ico
OutputBaseFilename=CodexLimitsWidget-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\bin\GPTUsageWidget.en.exe"; DestDir: "{app}"; DestName: "CodexLimitsWidget.exe"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "{src}\data\settings.json"; DestDir: "{app}\data"; Flags: external skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{userprograms}\Codex Limits Widget"; Filename: "{app}\CodexLimitsWidget.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\CodexLimitsWidget.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunEntry = 'CodexLimitsWidget';

procedure CurStepChanged(CurStep: TSetupStep);
var
  PreviousCommand, PortableCommand: String;
begin
  if CurStep = ssPostInstall then begin
    PortableCommand := '"' + ExpandConstant('{src}\CodexLimitsWidget.exe') + '"';
    if RegQueryStringValue(HKCU, RunKey, RunEntry, PreviousCommand) and
       (CompareText(PreviousCommand, PortableCommand) = 0) then
      RegWriteStringValue(HKCU, RunKey, RunEntry, '"' + ExpandConstant('{app}\CodexLimitsWidget.exe') + '"');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if (CurUninstallStep = usUninstall) and RegQueryStringValue(HKCU, RunKey, RunEntry, Command) and
     (CompareText(Command, '"' + ExpandConstant('{app}\CodexLimitsWidget.exe') + '"') = 0) then
    RegDeleteValue(HKCU, RunKey, RunEntry);
end;
