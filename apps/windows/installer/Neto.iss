#ifndef MyAppVersion
  #define MyAppVersion "0.0.7"
#endif

#ifndef PublishDir
  #define PublishDir "..\..\..\dist\Neto"
#endif

#ifndef DistDir
  #define DistDir "..\..\..\dist"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "Neto-windows-x64-setup"
#endif

[Setup]
AppId={{8E6B3A92-5C41-4D7E-9B14-7A2F4E8C1091}
AppName=Neto
AppVersion={#MyAppVersion}
AppVerName=Neto {#MyAppVersion}
AppPublisher=MoeclubM
AppPublisherURL=https://github.com/MoeclubM/Neto
AppSupportURL=https://github.com/MoeclubM/Neto/releases
AppUpdatesURL=https://github.com/MoeclubM/Neto/releases
DefaultDirName={autopf}\Neto
DefaultGroupName=Neto
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#DistDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\Neto.exe
UninstallDisplayName=Neto
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Neto"; Filename: "{app}\Neto.exe"
Name: "{autodesktop}\Neto"; Filename: "{app}\Neto.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Neto"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Neto.exe"; Description: "{cm:LaunchProgram,Neto}"; Flags: nowait postinstall skipifsilent

[Code]
function HasInstalledTunService(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NetoTunService');
end;

procedure RunServiceCommand(const Args: String);
var
  SvcExe: String;
  ResultCode: Integer;
begin
  SvcExe := ExpandConstant('{app}\neto-service.exe');
  if not FileExists(SvcExe) then Exit;
  if IsAdminInstallMode() then
    Exec(SvcExe, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    ShellExec('runas', SvcExe, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StopRunningNeto();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Neto.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if HasInstalledTunService() then
    RunServiceCommand('stop --id "Neto"');
end;

function InitializeSetup(): Boolean;
begin
  StopRunningNeto();
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and HasInstalledTunService() then
    RunServiceCommand('install --id "Neto"');
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Neto.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if HasInstalledTunService() then
      RunServiceCommand('uninstall --id "Neto"');
    if RegKeyExists(HKCU, 'Software\Neto\SystemProxy') then
    begin
      RegWriteDWordValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Internet Settings', 'ProxyEnable', 0);
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Internet Settings', 'ProxyServer');
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Neto');
    end;
  end;
end;
