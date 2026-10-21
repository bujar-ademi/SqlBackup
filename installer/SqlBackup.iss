#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef DisplayVersion
  #define DisplayVersion AppVersion
#endif
#ifndef PackageRoot
  #define PackageRoot "..\.artifacts\package"
#endif
#ifndef OutputRoot
  #define OutputRoot "..\.artifacts\installer"
#endif

[Setup]
AppId={{BC460299-789B-4166-8EC1-15834CC82B62}
AppName=SqlBackup
AppVersion={#DisplayVersion}
AppVerName=SqlBackup {#DisplayVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Bujar Ademi
AppPublisherURL=https://bujarademi.com
AppSupportURL=https://github.com/bujar-ademi/SqlBackup/issues
DefaultDirName={autopf}\SqlBackup
DefaultGroupName=SqlBackup
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.20348
OutputDir={#OutputRoot}
OutputBaseFilename=SqlBackup-Setup-{#DisplayVersion}-win-x64
SetupIconFile=..\src\SqlBackup.Desktop\Assets\sqlbackup.ico
UninstallDisplayIcon={app}\Desktop\SqlBackup.Desktop.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupMutex=Global\SqlBackup.Setup
InfoBeforeFile=setup-information.txt
LicenseFile={#PackageRoot}\Legal\Distribution-Terms.txt

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PackageRoot}\Legal\*"; DestDir: "{app}\Legal"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Desktop\*"; DestDir: "{app}\Desktop"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Setup\SqlBackup.Setup.exe"; DestDir: "{app}\Setup"; Flags: ignoreversion

[Icons]
Name: "{group}\SqlBackup"; Filename: "{app}\Desktop\SqlBackup.Desktop.exe"; WorkingDir: "{app}\Desktop"
Name: "{autodesktop}\SqlBackup"; Filename: "{app}\Desktop\SqlBackup.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Desktop\SqlBackup.Desktop.exe"; Description: "Open SqlBackup"; Flags: postinstall nowait skipifsilent

[Code]
var
  Prepared: Boolean;
  Committed: Boolean;
  RemovalPrepared: Boolean;
  Removed: Boolean;
  HelperResult: String;

function RunHelper(HelperPath, Operation: String): Boolean;
var
  ExitCode: Integer;
  ResultPath: String;
  Arguments: String;
  Output: AnsiString;
begin
  ResultPath := ExpandConstant('{tmp}\SqlBackup-setup-result.txt');
  DeleteFile(ResultPath);
  Arguments := Operation + ' "' + ExpandConstant('{app}') + '" "{#AppVersion}" "' + ResultPath + '"';
  Result := Exec(HelperPath, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Result := Result and (ExitCode = 0);
  HelperResult := 'Setup helper could not complete. Configuration and backups are retained. See the installation and recovery guide.';
  if LoadStringFromFile(ResultPath, Output) then
  begin
    HelperResult := String(Output);
  end;
  Log(Operation + ': ' + HelperResult);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if Prepared then
  begin
    Exit;
  end;
  ExtractTemporaryFile('SqlBackup.Setup.exe');
  Prepared := RunHelper(ExpandConstant('{tmp}\SqlBackup.Setup.exe'), 'prepare');
  if not Prepared then
  begin
    Result := HelperResult;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    Committed := RunHelper(ExpandConstant('{tmp}\SqlBackup.Setup.exe'), 'commit');
    if not Committed then
    begin
      RaiseException(HelperResult);
    end;
  end;
end;

procedure DeinitializeSetup;
begin
  if Prepared and not Committed then
  begin
    if not RunHelper(ExpandConstant('{tmp}\SqlBackup.Setup.exe'), 'rollback') then
    begin
      MsgBox('Automatic application rollback could not complete. ' + HelperResult, mbError, MB_OK);
    end;
  end;
end;

function InitializeUninstall: Boolean;
begin
  RemovalPrepared := RunHelper(ExpandConstant('{app}\Setup\SqlBackup.Setup.exe'), 'uninstall-prepare');
  Result := RemovalPrepared;
  if not Result then
  begin
    MsgBox(HelperResult, mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    Removed := RunHelper(ExpandConstant('{app}\Setup\SqlBackup.Setup.exe'), 'uninstall');
    if not Removed then
    begin
      RaiseException(HelperResult);
    end;
  end;
end;

procedure DeinitializeUninstall;
begin
  if RemovalPrepared and not Removed then
  begin
    if not RunHelper(ExpandConstant('{app}\Setup\SqlBackup.Setup.exe'), 'resume') then
    begin
      MsgBox('Service restart could not be confirmed. ' + HelperResult, mbError, MB_OK);
    end;
  end;
end;
