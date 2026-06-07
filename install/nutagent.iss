#define AppVersion "1.0.0"
#define ServiceName "NutAgent"
#define ServiceDisplay "NutAgent UPS Agent"
#define ServiceDesc "NUT-compatible UPS monitoring agent (NutAgent)"

[Setup]
AppName=NutAgent
AppVersion={#AppVersion}
AppPublisher=CrackerFormula
AppId={{6F3A2B1C-D4E5-4F60-8A7B-9C0D1E2F3A4B}
DefaultDirName={autopf64}\NutAgent
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=NutAgent-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName=NutAgent
UsedUserAreasWarning=no

[Files]
; BeforeInstall on the first file handles stop/cleanup for upgrade installs
Source: "..\NutAgent\bin\Release\net10.0-windows\win-x64\publish\ups-agent.exe"; \
    DestDir: "{app}"; BeforeInstall: PrepareForInstall; Flags: ignoreversion
Source: "..\NutAgent.Tray\bin\Release\net10.0-windows\win-x64\publish\ups-tray.exe"; \
    DestDir: "{app}"; Flags: ignoreversion
; Diagnostic dump tool — bundled so users can gather device-support info without
; cloning/building anything; see the README's "My UPS doesn't work right" section
Source: "..\HidDiag\bin\Release\net10.0\win-x64\publish\HidDiag.exe"; \
    DestDir: "{app}"; Flags: ignoreversion
; onlyifdoesntexist preserves the user's config (password, thresholds) on reinstall/upgrade
Source: "..\NutAgent\appsettings.json"; \
    DestDir: "{app}"; AfterInstall: PatchConfig; Flags: onlyifdoesntexist

[Run]
Filename: "{sys}\sc.exe"; \
    Parameters: "create {#ServiceName} binPath= ""{app}\ups-agent.exe"" start= auto DisplayName= ""{#ServiceDisplay}"""; \
    Flags: runhidden; StatusMsg: "Registering service..."
Filename: "{sys}\sc.exe"; \
    Parameters: "description {#ServiceName} ""{#ServiceDesc}"""; \
    Flags: runhidden
Filename: "{sys}\sc.exe"; \
    Parameters: "failure {#ServiceName} reset= 60 actions= restart/5000/restart/10000/restart/30000"; \
    Flags: runhidden
Filename: "{sys}\sc.exe"; \
    Parameters: "start {#ServiceName}"; \
    Flags: runhidden; StatusMsg: "Starting service..."
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""NutAgent NUT Server (TCP 3493)"" dir=in action=allow protocol=TCP localport=3493"; \
    Flags: runhidden; Check: IsServerMode; StatusMsg: "Adding firewall rule..."
Filename: "{app}\ups-tray.exe"; \
    Description: "Launch NutAgent tray app now"; \
    Flags: postinstall nowait skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "NutAgentTray"; \
    ValueData: """{app}\ups-tray.exe"""

[UninstallDelete]
; appsettings.json may have been user-modified so Inno won't track it — delete explicitly
Type: files; Name: "{app}\appsettings.json"

[Code]
var
  ModePage: TInputOptionWizardPage;
  RemoteHostPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  ModePage := CreateInputOptionPage(wpWelcome,
    'Installation Mode', 'How will this PC connect to the UPS?',
    'Select the mode for this installation:', True, False);
  ModePage.Add('Server  —  this PC has a UPS connected via USB');
  ModePage.Add('Client  —  this PC shares a UPS with another machine on the network');
  ModePage.SelectedValueIndex := 0;

  RemoteHostPage := CreateInputQueryPage(ModePage.ID,
    'Remote NUT Server', 'Enter the IP of the server PC.',
    'NutAgent will monitor the UPS through the remote NUT server:');
  RemoteHostPage.Add('Server IP address:', False);
end;

function IsServerMode: Boolean;
begin
  Result := ModePage.SelectedValueIndex = 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  if PageID = RemoteHostPage.ID then
    Result := IsServerMode
  else
    Result := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = RemoteHostPage.ID) and (Trim(RemoteHostPage.Values[0]) = '') then
  begin
    MsgBox('Please enter the NUT server IP address.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure PrepareForInstall;
var
  ResultCode: Integer;
begin
  // Kill tray app if running so the exe can be overwritten
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ups-tray.exe', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Stop and remove existing service (upgrade scenario — errors intentionally ignored)
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

procedure PatchConfig;
var
  Path, Content: String;
  Lines: TStringList;
begin
  Path := ExpandConstant('{app}\appsettings.json');
  Lines := TStringList.Create;
  try
    Lines.LoadFromFile(Path);
    Content := Lines.Text;

    if IsServerMode then
      StringChangeEx(Content, '"Mode": "Client"', '"Mode": "Server"', True)
    else
    begin
      StringChangeEx(Content, '"Mode": "Server"', '"Mode": "Client"', True);
      StringChangeEx(Content, '"RemoteHost": ""',
          '"RemoteHost": "' + Trim(RemoteHostPage.Values[0]) + '"', True);
    end;

    Lines.Text := Content;
    Lines.SaveToFile(Path);
  finally
    Lines.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ups-tray.exe', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'),
         'advfirewall firewall delete rule name="NutAgent NUT Server (TCP 3493)"', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
end;
