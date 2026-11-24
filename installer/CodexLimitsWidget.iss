#define AppName "Codex Limits Widget"
#define AppVersion "2.0.1"
#define CodexUrl "https://github.com/openai/codex/releases/download/rust-v0.172.0/codex-package-x86_64-pc-windows-msvc.tar.gz"
#define CodexSHA256 "b723674d7caf26a3f55121a02d6cad8f3ac8ddf5860747a6edde1c904cf593af"

[Setup]
AppId={{CA276763-EE51-45D5-B486-E34BAD5703EF}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion=2.0.1.0
VersionInfoProductName={#AppName}
DefaultDirName={localappdata}\Programs\Codex Limits Widget
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
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

[CustomMessages]
english.CodexDownloadTitle=Installing Codex CLI
english.CodexDownloadText=Downloading the official OpenAI package required to read usage limits.
english.CodexExtractFailed=Could not extract Codex CLI.
english.CodexInstallFailed=Codex CLI could not be installed.
russian.CodexDownloadTitle=Установка Codex CLI
russian.CodexDownloadText=Скачивается официальный пакет OpenAI для просмотра лимитов.
russian.CodexExtractFailed=Не удалось распаковать Codex CLI.
russian.CodexInstallFailed=Не удалось установить Codex CLI.

[Files]
Source: "..\dist\win-unpacked\resources\CodexLimitsBridge.exe"; Flags: dontcopy
Source: "{tmp}\codex-runtime\*"; DestDir: "{app}\resources\codex"; Flags: external skipifsourcedoesntexist ignoreversion recursesubdirs createallsubdirs
Source: "..\dist\win-unpacked\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "{src}\data\settings.json"; DestDir: "{app}\data"; Flags: external skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall
Source: "{src}\data\history.json"; DestDir: "{app}\data"; Flags: external skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall
Source: "{src}\data\history.json.bak"; DestDir: "{app}\data"; Flags: external skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall
Source: "{src}\data\alerts.json"; DestDir: "{app}\data"; Flags: external skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall
Source: "{src}\data\storage-migration.json"; DestDir: "{app}\data"; Flags: external skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{userprograms}\Codex Limits Widget"; Filename: "{app}\CodexLimitsWidget.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\CodexLimitsWidget.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunEntry = 'CodexLimitsWidget';
  CodexArchive = 'codex-runtime.tar.gz';

var
  CodexDownload: TDownloadWizardPage;
  CodexStaged: Boolean;

procedure InitializeWizard;
begin
  CodexDownload := CreateDownloadPage(
    ExpandConstant('{cm:CodexDownloadTitle}'), ExpandConstant('{cm:CodexDownloadText}'), nil);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  StageDir: String;
begin
  Result := '';
  StageDir := ExpandConstant('{tmp}\codex-runtime');
  if CodexStaged or
     FileExists(ExpandConstant('{app}\resources\codex\bin\codex.exe')) then
    Exit;

  try
    ExtractTemporaryFile('CodexLimitsBridge.exe');
    if Exec(ExpandConstant('{tmp}\CodexLimitsBridge.exe'), '--has-codex', '',
      SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0) then
      Exit;

    CodexDownload.Clear;
    CodexDownload.Add('{#CodexUrl}', CodexArchive, '{#CodexSHA256}');
    CodexDownload.Show;
    try
      CodexDownload.Download;
      if not ForceDirectories(StageDir) or
         not Exec(ExpandConstant('{sysnative}\tar.exe'), '-xzf "' +
           ExpandConstant('{tmp}\') + CodexArchive + '" -C "' + StageDir + '"',
           '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) or
         not FileExists(StageDir + '\bin\codex.exe') or
         not FileExists(StageDir + '\codex-package.json') then
        RaiseException(ExpandConstant('{cm:CodexExtractFailed}'));
      CodexStaged := True;
    finally
      CodexDownload.Hide;
    end;
  except
    Result := ExpandConstant('{cm:CodexInstallFailed}') + #13#10 + GetExceptionMessage;
  end;
end;

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
