; EInvoiceAPI-Setup.iss: Inno Setup 6 script for the ETA e-invoicing apps on IIS.
; Build with installer\build-installer.ps1 (publishes the apps, then runs ISCC).
;
; Wizard: apps -> 64/32-bit -> IIS site + URL path -> folders -> config.db password -> first Admin account
;         -> SAP Prod -> SAP Dev -> HTTPS certificate -> install.
; Before copying files it installs IIS (if missing and confirmed), downloads/installs the ASP.NET Core 10
; Hosting Bundle (if missing), prepares config.db and stops the app pools; afterwards it configures IIS.
;
; Unattended install (all optional; passwords on a command line are visible to other admins and in logs):
;   EInvoiceAPI-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /COMPONENTS="api,admin,notifications" /ARCH=x64
;     /SITE="Default Web Site" /BASEPATH=EInvoiceAPI /DIR="C:\inetpub\EInvoiceAPI" /DATADIR="C:\ETA\Data"
;     /NOTIFDIR="C:\EInvoice" /DBPASSWORD=... /ADMINUSER=... /ADMINPASSWORD=...
;     /SAPPRODURL=... /SAPPRODUSER=... /SAPPRODPASSWORD=... /SAPPRODTIMEOUT=3000 (and /SAPDEV...)
;     /CERT=<thumbprint> /HTTPSPORT=443 /HTTPSHOST=<name> /INSTALLIIS=yes /LOG="setup.log"

#define AppName "ETA E-Invoice API"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef HostingBundleUrl
  #define HostingBundleUrl "https://aka.ms/dotnet/10.0/dotnet-hosting-win.exe"
#endif
#define RegKey "Software\ETA\EInvoiceAPI"

[Setup]
AppId={{6E3B8F2A-4C1D-4E7A-9B52-3D1F0A7C9E41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ETA E-Invoicing
DefaultDirName=C:\inetpub\EInvoiceAPI
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=EInvoiceAPI-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
UninstallDisplayName={#AppName}

[Types]
Name: "full"; Description: "All apps"
Name: "custom"; Description: "Choose apps"; Flags: iscustom

[Components]
Name: "api"; Description: "EInvoiceAPI: invoice signing and submission API  (/<path>)"; Types: full custom
Name: "admin"; Description: "EInvoiceAPI Admin: configuration web site  (/<path>/Admin)"; Types: full
Name: "notifications"; Description: "ETANotifications: receives ETA notifications, forwards to SAP  (/<path>/ETANotifications)"; Types: full

[Files]
Source: "{#PublishDir}\x64\EInvoiceAPI\*"; DestDir: "{app}\EInvoiceAPI"; Components: api; Check: IsArch('x64'); Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\x86\EInvoiceAPI\*"; DestDir: "{app}\EInvoiceAPI"; Components: api; Check: IsArch('x86'); Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\x64\EInvoiceAPIAdmin\*"; DestDir: "{app}\Admin"; Components: admin; Check: IsArch('x64'); Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\x86\EInvoiceAPIAdmin\*"; DestDir: "{app}\Admin"; Components: admin; Check: IsArch('x86'); Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\x64\IncomeTaxWebAPI\*"; DestDir: "{app}\ETANotifications"; Components: notifications; Check: IsArch('x64'); Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\x86\IncomeTaxWebAPI\*"; DestDir: "{app}\ETANotifications"; Components: notifications; Check: IsArch('x86'); Flags: ignoreversion recursesubdirs createallsubdirs
; Kept for re-running by hand and for the uninstaller.
Source: "..\deploy\Setup-IIS.ps1"; DestDir: "{app}\deploy"; Flags: ignoreversion
; Used during setup only.
Source: "SetupHelpers.ps1"; Flags: dontcopy
Source: "{#PublishDir}\SetupTool.zip"; Flags: dontcopy

[Registry]
; Read by the uninstaller to remove the IIS applications and app pools.
Root: HKLM; Subkey: "{#RegKey}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "{#RegKey}"; ValueType: string; ValueName: "SiteName"; ValueData: "{code:GetSiteName}"
Root: HKLM; Subkey: "{#RegKey}"; ValueType: string; ValueName: "BasePath"; ValueData: "{code:GetBasePath}"
Root: HKLM; Subkey: "{#RegKey}"; ValueType: string; ValueName: "Apps"; ValueData: "{code:GetAppList}"
Root: HKLM; Subkey: "{#RegKey}"; ValueType: string; ValueName: "Arch"; ValueData: "{code:GetArch}"
Root: HKLM; Subkey: "{#RegKey}"; ValueType: string; ValueName: "DatabasePath"; ValueData: "{code:GetDatabasePath}"

[Run]
Filename: "{code:GetAdminUrl}"; Description: "Open the Admin site"; Flags: postinstall shellexec skipifsilent nowait unchecked; Check: IsAdminSelected

[Code]
const
  DefaultSite = 'Default Web Site';
  DefaultBasePath = 'EInvoiceAPI';
  DefaultDataDir = 'C:\ETA\Data';
  DefaultNotifDir = 'C:\EInvoice';
  DefaultSapProdUrl = 'https://vhfrgprdai01.sap.i.fresh.com.eg:44300/zeinvoice?sap-client=300';
  DefaultSapDevUrl = 'https://vhfrgdevci.sap.i.fresh.com.eg:44300/zeinvoice?sap-client=300';

var
  ArchPage: TInputOptionWizardPage;
  IisPage: TWizardPage;
  SiteCombo: TNewComboBox;
  BasePathEdit: TNewEdit;
  FolderPage: TInputDirWizardPage;
  DbPage: TWizardPage;
  DbStatusLabel: TNewStaticText;
  DbGenerateRadio, DbEnterRadio: TNewRadioButton;
  DbGeneratedEdit: TNewEdit;
  DbGeneratedNote: TNewStaticText;
  DbPasswordLabel, DbConfirmLabel: TNewStaticText;
  DbPasswordEdit, DbConfirmEdit: TPasswordEdit;
  AdminPage: TInputQueryWizardPage;
  SapProdPage, SapDevPage: TInputQueryWizardPage;
  HttpsPage: TWizardPage;
  HttpsCheck: TNewCheckBox;
  CertLabel, PortLabel, HostLabel, NoCertLabel: TNewStaticText;
  CertCombo: TNewComboBox;
  HttpsPortEdit, HttpsHostEdit: TNewEdit;
  ProgressPage: TOutputProgressWizardPage;

  CertThumbs: TArrayOfString;
  CertsLoaded, HelpersExtracted, ToolExtracted, InstallIisRequested, DbExists, SapPrefilled: Boolean;
  GeneratedPassword, PoolPasswordTriedFor, CertParam: String;

function SetEnvironmentVariable(lpName: String; lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

{ ---------- small helpers ---------- }

function Param(const Name, Default: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '}');
  if Result = '' then Result := Default;
end;

function PowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function IisInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\inetsrv\Microsoft.Web.Administration.dll'));
end;

function DirMatches(const Pattern: String): Boolean;
var
  FR: TFindRec;
begin
  Result := FindFirst(Pattern, FR);
  if Result then FindClose(FR);
end;

function GetArch(Value: String): String;
begin
  if ArchPage.Values[1] then Result := 'x86' else Result := 'x64';
end;

function IsArch(const Arch: String): Boolean;
begin
  Result := GetArch('') = Arch;
end;

function DotNetRuntimeInstalled: Boolean;
begin
  Result := DirMatches(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\10.*'));
end;

function HostingBundleInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\inetsrv\aspnetcorev2.dll'))
    and DirMatches(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.AspNetCore.App\10.*'));
  if Result and IsArch('x86') then
    Result := DirMatches(ExpandConstant('{commonpf32}\dotnet\shared\Microsoft.AspNetCore.App\10.*'));
end;

function IsAbsolutePath(const S: String): Boolean;
begin
  Result := ((Length(S) >= 3) and (S[2] = ':') and (S[3] = '\')) or (Copy(S, 1, 2) = '\\');
end;

function IsValidBasePath(const S: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((C >= '0') and (C <= '9')) or (C = '-') or (C = '_')) then
      Result := False;
  end;
end;

function IsHttpUrl(const S: String): Boolean;
begin
  Result := (Pos('http://', LowerCase(S)) = 1) or (Pos('https://', LowerCase(S)) = 1);
end;

function GetSiteName(Value: String): String;
begin
  Result := Trim(SiteCombo.Text);
end;

function GetBasePath(Value: String): String;
begin
  Result := Trim(BasePathEdit.Text);
end;

function GetWebRoot: String;
begin
  Result := RemoveBackslashUnlessRoot(Trim(FolderPage.Values[0]));
end;

function GetDataDir: String;
begin
  Result := RemoveBackslashUnlessRoot(Trim(FolderPage.Values[1]));
end;

function GetNotifDir: String;
begin
  Result := RemoveBackslashUnlessRoot(Trim(FolderPage.Values[2]));
end;

function GetDatabasePath(Value: String): String;
begin
  Result := AddBackslash(GetDataDir) + 'config.db';
end;

function GetAppList(Value: String): String;
begin
  Result := '';
  if WizardIsComponentSelected('api') then Result := Result + ',EInvoiceAPI';
  if WizardIsComponentSelected('admin') then Result := Result + ',Admin';
  if WizardIsComponentSelected('notifications') then Result := Result + ',ETANotifications';
  if Result <> '' then Delete(Result, 1, 1);
end;

function IsAdminSelected: Boolean;
begin
  Result := WizardIsComponentSelected('admin');
end;

function GetAdminUrl(Value: String): String;
begin
  Result := 'http://localhost/' + GetBasePath('') + '/Admin';
end;

procedure LogFile(const FileName: String);
var
  Lines: TArrayOfString;
  I: Integer;
begin
  if LoadStringsFromFile(FileName, Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      Log('  ' + Lines[I]);
end;

{ Runs SetupHelpers.ps1 -Action <Action>; its output lines are returned in Lines. }
function RunHelper(const Action, ExtraArgs: String; var Lines: TArrayOfString): Boolean;
var
  OutFile: String;
  ResultCode: Integer;
begin
  if not HelpersExtracted then
  begin
    ExtractTemporaryFile('SetupHelpers.ps1');
    HelpersExtracted := True;
  end;
  OutFile := ExpandConstant('{tmp}\helper-out.txt');
  DeleteFile(OutFile);
  SetArrayLength(Lines, 0);
  Result := Exec(PowerShellExe, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File '
      + AddQuotes(ExpandConstant('{tmp}\SetupHelpers.ps1')) + ' -Action ' + Action + ' -Out ' + AddQuotes(OutFile) + ' ' + ExtraArgs,
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if FileExists(OutFile) then
  begin
    LoadStringsFromFile(OutFile, Lines);
    DeleteFile(OutFile); // may contain a password
  end;
  Log('Helper ' + Action + ': ' + IntToStr(ResultCode));
end;

function FirstLine(const Lines: TArrayOfString): String;
begin
  if GetArrayLength(Lines) > 0 then Result := Trim(Lines[0]) else Result := '';
end;

// Unpacks ETA.SetupTool (shipped as SetupTool.zip) into {tmp}\SetupTool once.
function SetupToolExe: String;
var
  Lines: TArrayOfString;
begin
  Result := ExpandConstant('{tmp}\SetupTool\ETA.SetupTool.exe');
  if ToolExtracted then Exit;
  ExtractTemporaryFile('SetupTool.zip');
  RunHelper('ExpandZip', '-Zip ' + AddQuotes(ExpandConstant('{tmp}\SetupTool.zip')) + ' -Destination ' + AddQuotes(ExpandConstant('{tmp}\SetupTool')), Lines);
  ToolExtracted := FileExists(Result);
end;

{ Runs ETA.SetupTool.exe with the database settings in its environment. Returns the exit code. }
function RunSetupTool(const Command: String; IncludeAccountAndSap: Boolean): Integer;
var
  LogPath: String;
  ExitCode: Integer;
begin
  LogPath := ExpandConstant('{tmp}\setuptool.log');
  DeleteFile(LogPath);
  SetEnvironmentVariable('ETA_SETUP_LOG', LogPath);
  SetEnvironmentVariable('ETA_DB_PATH', GetDatabasePath(''));
  if DbExists then
    SetEnvironmentVariable('ETA_DB_PASSWORD', DbPasswordEdit.Text)
  else if DbGenerateRadio.Checked then
    SetEnvironmentVariable('ETA_DB_PASSWORD', GeneratedPassword)
  else
    SetEnvironmentVariable('ETA_DB_PASSWORD', DbPasswordEdit.Text);
  if IncludeAccountAndSap then
  begin
    SetEnvironmentVariable('ETA_ADMIN_USER', AdminPage.Values[0]);
    SetEnvironmentVariable('ETA_ADMIN_PASSWORD', AdminPage.Values[1]);
    if WizardIsComponentSelected('notifications') then
    begin
      SetEnvironmentVariable('ETA_SAP_PROD_URL', Trim(SapProdPage.Values[0]));
      SetEnvironmentVariable('ETA_SAP_PROD_USER', Trim(SapProdPage.Values[1]));
      SetEnvironmentVariable('ETA_SAP_PROD_PASSWORD', SapProdPage.Values[2]);
      SetEnvironmentVariable('ETA_SAP_PROD_TIMEOUT', Trim(SapProdPage.Values[3]));
      SetEnvironmentVariable('ETA_SAP_DEV_URL', Trim(SapDevPage.Values[0]));
      SetEnvironmentVariable('ETA_SAP_DEV_USER', Trim(SapDevPage.Values[1]));
      SetEnvironmentVariable('ETA_SAP_DEV_PASSWORD', SapDevPage.Values[2]);
      SetEnvironmentVariable('ETA_SAP_DEV_TIMEOUT', Trim(SapDevPage.Values[3]));
    end;
  end;
  if Exec(SetupToolExe, Command, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := ExitCode
  else
    Result := -1;
  { Clear secrets from this process' environment. }
  SetEnvironmentVariable('ETA_DB_PASSWORD', '');
  SetEnvironmentVariable('ETA_ADMIN_PASSWORD', '');
  SetEnvironmentVariable('ETA_SAP_PROD_PASSWORD', '');
  SetEnvironmentVariable('ETA_SAP_DEV_PASSWORD', '');
  Log('ETA.SetupTool ' + Command + ' exit code ' + IntToStr(Result) + ':');
  LogFile(LogPath);
end;

// Runs deploy\Setup-IIS.ps1 (extracted to {app}\deploy) with the given extra arguments.
function RunSetupIis(const ExtraArgs: String; PassPassword: Boolean): Boolean;
var
  ResultCode: Integer;
  Transcript, Script: String;
begin
  Script := ExpandConstant('{tmp}\Setup-IIS.ps1');
  if not FileExists(Script) then ExtractTemporaryFile('Setup-IIS.ps1');
  Transcript := ExpandConstant('{tmp}\setup-iis.log');
  DeleteFile(Transcript);
  SetEnvironmentVariable('ETA_SETUP_TRANSCRIPT', Transcript);
  if PassPassword then
  begin
    if DbExists then SetEnvironmentVariable('ETA_DB_PASSWORD', DbPasswordEdit.Text)
    else if DbGenerateRadio.Checked then SetEnvironmentVariable('ETA_DB_PASSWORD', GeneratedPassword)
    else SetEnvironmentVariable('ETA_DB_PASSWORD', DbPasswordEdit.Text);
  end;
  Result := Exec(PowerShellExe, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + AddQuotes(Script)
      + ' -SiteName ' + AddQuotes(GetSiteName('')) + ' -BasePath ' + GetBasePath('') + ' -Apps ' + GetAppList('') + ' ' + ExtraArgs,
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  SetEnvironmentVariable('ETA_DB_PASSWORD', '');
  SetEnvironmentVariable('ETA_SETUP_TRANSCRIPT', '');
  Log('Setup-IIS.ps1 exit code ' + IntToStr(ResultCode) + ':');
  LogFile(Transcript);
end;

{ ---------- dynamic page state ---------- }

procedure UpdateDbControls;
begin
  DbGenerateRadio.Visible := not DbExists;
  DbEnterRadio.Visible := not DbExists;
  DbGeneratedEdit.Visible := (not DbExists) and DbGenerateRadio.Checked;
  DbGeneratedNote.Visible := DbGeneratedEdit.Visible;
  DbPasswordLabel.Visible := DbExists or DbEnterRadio.Checked;
  DbPasswordEdit.Visible := DbPasswordLabel.Visible;
  DbConfirmLabel.Visible := (not DbExists) and DbEnterRadio.Checked;
  DbConfirmEdit.Visible := DbConfirmLabel.Visible;
  if DbExists then
  begin
    DbPasswordLabel.Top := DbGenerateRadio.Top;
    DbPasswordEdit.Top := DbPasswordLabel.Top + ScaleY(18);
  end
  else
  begin
    DbPasswordLabel.Top := DbEnterRadio.Top + ScaleY(26);
    DbPasswordEdit.Top := DbPasswordLabel.Top + ScaleY(18);
  end;
  DbConfirmLabel.Top := DbPasswordEdit.Top + ScaleY(30);
  DbConfirmEdit.Top := DbConfirmLabel.Top + ScaleY(18);
end;

{ Called whenever the database page is reached (and before installing, for silent installs). }
procedure RefreshDbState;
var
  Lines: TArrayOfString;
  Pools: TArrayOfString;
  I: Integer;
begin
  DbExists := FileExists(GetDatabasePath(''));
  if DbExists then
  begin
    DbStatusLabel.Caption := 'An existing database was found at ' + GetDatabasePath('') + '. It will be kept and upgraded. '
      + 'Enter its password (the same one the apps already use).';
    { On upgrade, reuse the password IIS already has for the app pools. }
    if (DbPasswordEdit.Text = '') and IisInstalled and (PoolPasswordTriedFor <> GetBasePath('')) then
    begin
      PoolPasswordTriedFor := GetBasePath('');
      SetArrayLength(Pools, 3);
      Pools[0] := GetBasePath('');
      Pools[1] := GetBasePath('') + '-Admin';
      Pools[2] := GetBasePath('') + '-ETANotifications';
      for I := 0 to 2 do
        if DbPasswordEdit.Text = '' then
          if RunHelper('GetPoolPassword', '-Pool ' + AddQuotes(Pools[I]), Lines) and (FirstLine(Lines) <> '') then
          begin
            DbPasswordEdit.Text := FirstLine(Lines);
            DbStatusLabel.Caption := DbStatusLabel.Caption + ' It was read from the existing IIS app pool ' + Pools[I] + '.';
          end;
    end;
  end
  else
  begin
    DbStatusLabel.Caption := 'A new database will be created at ' + GetDatabasePath('') + '. '
      + 'All apps use the same password to open it (at least 32 characters).';
    if GeneratedPassword = '' then
    begin
      if RunHelper('NewPassword', '', Lines) then GeneratedPassword := FirstLine(Lines);
      DbGeneratedEdit.Text := GeneratedPassword;
    end;
  end;
  UpdateDbControls;
end;

procedure DbRadioClick(Sender: TObject);
begin
  UpdateDbControls;
end;

procedure UpdateHttpsControls;
begin
  CertCombo.Enabled := HttpsCheck.Checked and (CertCombo.Items.Count > 0);
  HttpsPortEdit.Enabled := HttpsCheck.Checked;
  HttpsHostEdit.Enabled := HttpsCheck.Checked;
end;

procedure HttpsCheckClick(Sender: TObject);
begin
  UpdateHttpsControls;
end;

procedure LoadCertificates;
var
  Lines: TArrayOfString;
  I, Sep: Integer;
  Thumb, Rest: String;
begin
  if CertsLoaded then Exit;
  CertsLoaded := True;
  CertCombo.Items.Clear;
  SetArrayLength(CertThumbs, 0);
  if RunHelper('ListCertificates', '', Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Sep := Pos('|', Lines[I]);
      if Sep > 0 then
      begin
        Thumb := Copy(Lines[I], 1, Sep - 1);
        Rest := Copy(Lines[I], Sep + 1, Length(Lines[I]));
        StringChangeEx(Rest, '|', '  (expires ', True);
        SetArrayLength(CertThumbs, GetArrayLength(CertThumbs) + 1);
        CertThumbs[GetArrayLength(CertThumbs) - 1] := Thumb;
        CertCombo.Items.Add(Rest + ')  ' + Thumb);
        if CompareText(Thumb, CertParam) = 0 then CertCombo.ItemIndex := CertCombo.Items.Count - 1;
      end;
    end;
  if (CertCombo.ItemIndex < 0) and (CertCombo.Items.Count > 0) then CertCombo.ItemIndex := 0;
  NoCertLabel.Visible := CertCombo.Items.Count = 0;
  UpdateHttpsControls;
end;

function GetCertThumbprint: String;
begin
  Result := '';
  if not HttpsCheck.Checked then Exit;
  if (CertCombo.ItemIndex >= 0) and (CertCombo.ItemIndex < GetArrayLength(CertThumbs)) then
    Result := CertThumbs[CertCombo.ItemIndex]
  else
    Result := CertParam;
end;

{ ---------- validation (used by the Next buttons and again before installing) ---------- }

function ValidatePage(PageID: Integer): String;
var
  Timeout: Integer;
  Lines: TArrayOfString;
  I: Integer;
  Found: Boolean;
  SapPage: TInputQueryWizardPage;
  SapName: String;
begin
  Result := '';
  if PageID = wpSelectComponents then
  begin
    if GetAppList('') = '' then Result := 'Select at least one app.';
  end
  else if PageID = IisPage.ID then
  begin
    if GetSiteName('') = '' then Result := 'Enter the IIS site name.'
    else if not IsValidBasePath(GetBasePath('')) then
      Result := 'The URL path may contain only letters, digits, "-" and "_" (for example EInvoiceAPI).'
    else if IisInstalled then
    begin
      Found := False;
      if RunHelper('ListSites', '', Lines) then
        for I := 0 to GetArrayLength(Lines) - 1 do
          if CompareText(Trim(Lines[I]), GetSiteName('')) = 0 then Found := True;
      if not Found then Result := 'IIS has no site named "' + GetSiteName('') + '".';
    end
    else if CompareText(GetSiteName(''), DefaultSite) <> 0 then
      Result := 'IIS is not installed yet, so only "' + DefaultSite + '" (created with IIS) can be used.';
  end
  else if PageID = FolderPage.ID then
  begin
    if not (IsAbsolutePath(GetWebRoot) and IsAbsolutePath(GetDataDir) and IsAbsolutePath(GetNotifDir)) then
      Result := 'Enter full folder paths (for example C:\ETA\Data).';
  end
  else if PageID = DbPage.ID then
  begin
    if DbExists or DbEnterRadio.Checked then
    begin
      if Length(DbPasswordEdit.Text) < 32 then Result := 'The database password must be at least 32 characters.'
      else if (not DbExists) and (DbPasswordEdit.Text <> DbConfirmEdit.Text) then Result := 'The passwords do not match.'
      else if DbExists and DotNetRuntimeInstalled then
        case RunSetupTool('check', False) of
          0: ;
          2: Result := 'This password does not open the existing database.';
        else
          Result := 'Could not open the existing database (see the setup log).';
        end;
    end
    else if Length(GeneratedPassword) < 32 then
      Result := 'Could not generate a password. Choose "Use this password" and enter one.';
  end
  else if PageID = AdminPage.ID then
  begin
    if DbExists then Exit;
    if (Trim(AdminPage.Values[0]) = '') or (Length(Trim(AdminPage.Values[0])) > 100) then Result := 'Enter a user name (up to 100 characters).'
    else if (Length(AdminPage.Values[1]) < 12) or (Length(AdminPage.Values[1]) > 256) then Result := 'The Admin password must be 12 to 256 characters.'
    else if AdminPage.Values[1] <> AdminPage.Values[2] then Result := 'The Admin passwords do not match.';
  end
  else if (PageID = SapProdPage.ID) or (PageID = SapDevPage.ID) then
  begin
    if not WizardIsComponentSelected('notifications') then Exit;
    if PageID = SapProdPage.ID then begin SapPage := SapProdPage; SapName := 'Prod'; end
    else begin SapPage := SapDevPage; SapName := 'Dev'; end;
    if (Trim(SapPage.Values[0]) <> '') and not IsHttpUrl(Trim(SapPage.Values[0])) then
      Result := 'The SAP ' + SapName + ' URL must start with http:// or https://.'
    else if Trim(SapPage.Values[3]) <> '' then
    begin
      Timeout := StrToIntDef(Trim(SapPage.Values[3]), -1);
      if (Timeout < 500) or (Timeout > 120000) then Result := 'The SAP ' + SapName + ' timeout must be 500 to 120000 ms.';
    end;
  end
  else if PageID = HttpsPage.ID then
  begin
    if HttpsCheck.Checked then
    begin
      if GetCertThumbprint = '' then Result := 'Select a certificate, or untick "Add an HTTPS binding".'
      else if (StrToIntDef(Trim(HttpsPortEdit.Text), 0) < 1) or (StrToIntDef(Trim(HttpsPortEdit.Text), 0) > 65535) then
        Result := 'Enter an HTTPS port between 1 and 65535.';
    end;
  end;
end;

{ ---------- wizard ---------- }

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IisInstalled then
  begin
    if WizardSilent then
      InstallIisRequested := CompareText(Param('INSTALLIIS', 'no'), 'yes') = 0
    else
      InstallIisRequested := MsgBox('IIS (Internet Information Services) is not installed on this computer.' + #13#10#13#10
        + 'Install it now as part of this setup?', mbConfirmation, MB_YESNO) = IDYES;
    if not InstallIisRequested then
    begin
      SuppressibleMsgBox('IIS is required. Install IIS (Web Server role), then run this setup again.', mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

function NewLabel(Page: TWizardPage; const Caption: String; Top: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(Page);
  Result.Parent := Page.Surface;
  Result.Caption := Caption;
  Result.Top := Top;
  Result.Width := Page.SurfaceWidth;
  Result.WordWrap := True;
  Result.AutoSize := True;
end;

function NewEdit(Page: TWizardPage; const Text: String; Top: Integer): TNewEdit;
begin
  Result := TNewEdit.Create(Page);
  Result.Parent := Page.Surface;
  Result.Text := Text;
  Result.Top := Top;
  Result.Width := Page.SurfaceWidth;
end;

function NewPasswordEdit(Page: TWizardPage; Top: Integer): TPasswordEdit;
begin
  Result := TPasswordEdit.Create(Page);
  Result.Parent := Page.Surface;
  Result.Top := Top;
  Result.Width := Page.SurfaceWidth;
end;

procedure InitializeWizard;
var
  Lines: TArrayOfString;
  I: Integer;
  Site: String;
begin
  { 1. Platform }
  ArchPage := CreateInputOptionPage(wpSelectComponents, 'Platform', 'Choose 64-bit or 32-bit',
    'The USB signing token driver (eps2003csp11.dll) is 32-bit. Choose 32-bit if this server signs invoices with the token.',
    True, False);
  ArchPage.Add('64-bit (x64)');
  ArchPage.Add('32-bit (x86): required for USB token signing');
  if CompareText(Param('ARCH', GetPreviousData('Arch', 'x64')), 'x86') = 0 then ArchPage.Values[1] := True else ArchPage.Values[0] := True;

  { 2. IIS site and URL path }
  IisPage := CreateCustomPage(ArchPage.ID, 'IIS', 'Choose the IIS site and URL path');
  NewLabel(IisPage, 'IIS site:', 0);
  SiteCombo := TNewComboBox.Create(IisPage);
  SiteCombo.Parent := IisPage.Surface;
  SiteCombo.Style := csDropDown;
  SiteCombo.Top := ScaleY(18);
  SiteCombo.Width := IisPage.SurfaceWidth;
  if IisInstalled and RunHelper('ListSites', '', Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      if Trim(Lines[I]) <> '' then SiteCombo.Items.Add(Trim(Lines[I]));
  if SiteCombo.Items.Count = 0 then SiteCombo.Items.Add(DefaultSite);
  Site := Param('SITE', GetPreviousData('SiteName', DefaultSite));
  SiteCombo.Text := Site;
  NewLabel(IisPage, 'URL path:', ScaleY(56));
  BasePathEdit := NewEdit(IisPage, Param('BASEPATH', GetPreviousData('BasePath', DefaultBasePath)), ScaleY(74));
  NewLabel(IisPage, 'The apps will be at http(s)://<server>/<path>, /<path>/Admin and /<path>/ETANotifications.', ScaleY(104));

  { 3. Folders }
  FolderPage := CreateInputDirPage(IisPage.ID, 'Folders', 'Where should the apps and their data go?',
    'The apps go in sub-folders of the first folder (EInvoiceAPI, Admin, ETANotifications). '
    + 'Keep the database folder outside the web folders.', False, '');
  FolderPage.Add('Web applications folder:');
  FolderPage.Add('Database folder (config.db):');
  FolderPage.Add('Notification logs folder (ETANotifications SQLite files):');
  FolderPage.Values[0] := WizardForm.DirEdit.Text;
  FolderPage.Values[1] := Param('DATADIR', GetPreviousData('DataDir', DefaultDataDir));
  FolderPage.Values[2] := Param('NOTIFDIR', GetPreviousData('NotifDir', DefaultNotifDir));

  { 4. Database password }
  DbPage := CreateCustomPage(FolderPage.ID, 'Database', 'Password for the shared configuration database');
  DbStatusLabel := NewLabel(DbPage, '', 0);
  DbGenerateRadio := TNewRadioButton.Create(DbPage);
  DbGenerateRadio.Parent := DbPage.Surface;
  DbGenerateRadio.Caption := 'Generate a strong random password (recommended)';
  DbGenerateRadio.Top := ScaleY(56);
  DbGenerateRadio.Width := DbPage.SurfaceWidth;
  DbGenerateRadio.OnClick := @DbRadioClick;
  DbGeneratedEdit := NewEdit(DbPage, '', ScaleY(78));
  DbGeneratedEdit.ReadOnly := True;
  DbGeneratedNote := NewLabel(DbPage, 'Copy it and keep it somewhere safe. Setup also stores it in the IIS app pool settings.', ScaleY(104));
  DbEnterRadio := TNewRadioButton.Create(DbPage);
  DbEnterRadio.Parent := DbPage.Surface;
  DbEnterRadio.Caption := 'Use this password:';
  DbEnterRadio.Top := ScaleY(136);
  DbEnterRadio.Width := DbPage.SurfaceWidth;
  DbEnterRadio.OnClick := @DbRadioClick;
  DbPasswordLabel := NewLabel(DbPage, 'Password (at least 32 characters):', ScaleY(162));
  DbPasswordEdit := NewPasswordEdit(DbPage, ScaleY(180));
  DbConfirmLabel := NewLabel(DbPage, 'Confirm password:', ScaleY(210));
  DbConfirmEdit := NewPasswordEdit(DbPage, ScaleY(228));
  DbPasswordEdit.Text := Param('DBPASSWORD', '');
  DbConfirmEdit.Text := DbPasswordEdit.Text;
  if DbPasswordEdit.Text <> '' then DbEnterRadio.Checked := True else DbGenerateRadio.Checked := True;

  { 5. First Admin account (new database only) }
  AdminPage := CreateInputQueryPage(DbPage.ID, 'Admin account', 'First account for the Admin site',
    'The database is new, so create the first account for signing in to the Admin site.');
  AdminPage.Add('User name:', False);
  AdminPage.Add('Password (12 characters or more):', True);
  AdminPage.Add('Confirm password:', True);
  AdminPage.Values[0] := Param('ADMINUSER', 'admin');
  AdminPage.Values[1] := Param('ADMINPASSWORD', '');
  AdminPage.Values[2] := AdminPage.Values[1];

  { 6-7. SAP endpoints (ETANotifications) }
  SapProdPage := CreateInputQueryPage(AdminPage.ID, 'SAP endpoint: Prod', 'Where ETANotifications forwards ETA notifications',
    'Used by /<path>/ETANotifications/api/einvoice/... Leave a field blank to keep its current value. '
    + 'These can be changed later in Admin > SAP Endpoints.');
  SapProdPage.Add('SAP URL:', False);
  SapProdPage.Add('User name:', False);
  SapProdPage.Add('Password:', True);
  SapProdPage.Add('Timeout in milliseconds (default 3000):', False);
  SapProdPage.Values[0] := Param('SAPPRODURL', '');
  SapProdPage.Values[1] := Param('SAPPRODUSER', '');
  SapProdPage.Values[2] := Param('SAPPRODPASSWORD', '');
  SapProdPage.Values[3] := Param('SAPPRODTIMEOUT', '');

  SapDevPage := CreateInputQueryPage(SapProdPage.ID, 'SAP endpoint: Dev', 'Used by the /dev notification routes',
    'Used by /<path>/ETANotifications/api/einvoice/dev/... Leave a field blank to keep its current value.');
  SapDevPage.Add('SAP URL:', False);
  SapDevPage.Add('User name:', False);
  SapDevPage.Add('Password:', True);
  SapDevPage.Add('Timeout in milliseconds (default 3000):', False);
  SapDevPage.Values[0] := Param('SAPDEVURL', '');
  SapDevPage.Values[1] := Param('SAPDEVUSER', '');
  SapDevPage.Values[2] := Param('SAPDEVPASSWORD', '');
  SapDevPage.Values[3] := Param('SAPDEVTIMEOUT', '');

  { 8. HTTPS binding }
  HttpsPage := CreateCustomPage(SapDevPage.ID, 'HTTPS', 'Optionally add an HTTPS binding to the IIS site');
  HttpsCheck := TNewCheckBox.Create(HttpsPage);
  HttpsCheck.Parent := HttpsPage.Surface;
  HttpsCheck.Caption := 'Add an HTTPS binding to the site using an existing certificate';
  HttpsCheck.Width := HttpsPage.SurfaceWidth;
  HttpsCheck.OnClick := @HttpsCheckClick;
  CertLabel := NewLabel(HttpsPage, 'Certificate (Local Computer > Personal store):', ScaleY(30));
  CertCombo := TNewComboBox.Create(HttpsPage);
  CertCombo.Parent := HttpsPage.Surface;
  CertCombo.Style := csDropDownList;
  CertCombo.Top := ScaleY(48);
  CertCombo.Width := HttpsPage.SurfaceWidth;
  NoCertLabel := NewLabel(HttpsPage, 'No certificates with a private key were found. Import one into Local Computer > Personal, '
    + 'or add the binding later in IIS Manager.', ScaleY(76));
  NoCertLabel.Visible := False;
  PortLabel := NewLabel(HttpsPage, 'Port:', ScaleY(112));
  HttpsPortEdit := NewEdit(HttpsPage, Param('HTTPSPORT', GetPreviousData('HttpsPort', '443')), ScaleY(130));
  HttpsPortEdit.Width := ScaleX(80);
  HostLabel := NewLabel(HttpsPage, 'Host name (optional; for example eta.company.local, uses SNI):', ScaleY(162));
  HttpsHostEdit := NewEdit(HttpsPage, Param('HTTPSHOST', GetPreviousData('HttpsHost', '')), ScaleY(180));
  CertParam := Param('CERT', '');
  HttpsCheck.Checked := CertParam <> '';
  UpdateHttpsControls;

  ProgressPage := CreateOutputProgressPage('Preparing the server', 'Please wait while setup prepares IIS and the database.');
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'Arch', GetArch(''));
  SetPreviousData(PreviousDataKey, 'SiteName', GetSiteName(''));
  SetPreviousData(PreviousDataKey, 'BasePath', GetBasePath(''));
  SetPreviousData(PreviousDataKey, 'DataDir', GetDataDir);
  SetPreviousData(PreviousDataKey, 'NotifDir', GetNotifDir);
  SetPreviousData(PreviousDataKey, 'HttpsPort', Trim(HttpsPortEdit.Text));
  SetPreviousData(PreviousDataKey, 'HttpsHost', Trim(HttpsHostEdit.Text));
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = AdminPage.ID then Result := DbExists
  else if (PageID = SapProdPage.ID) or (PageID = SapDevPage.ID) then Result := not WizardIsComponentSelected('notifications');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = DbPage.ID then RefreshDbState
  else if CurPageID = HttpsPage.ID then LoadCertificates
  else if (CurPageID = SapProdPage.ID) and (not SapPrefilled) then
  begin
    { On a new database, show the default SAP URLs (they are seeded anyway). }
    SapPrefilled := True;
    if not DbExists then
    begin
      if SapProdPage.Values[0] = '' then SapProdPage.Values[0] := DefaultSapProdUrl;
      if SapDevPage.Values[0] = '' then SapDevPage.Values[0] := DefaultSapDevUrl;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  if CurPageID = FolderPage.ID then
    WizardForm.DirEdit.Text := GetWebRoot; // {app} = web applications folder
  if CurPageID = DbPage.ID then RefreshDbState; { silent installs may not run CurPageChanged }
  if CurPageID = HttpsPage.ID then LoadCertificates;
  Error := ValidatePage(CurPageID);
  Result := Error = '';
  { Inno Setup also "clicks" Next during silent installs, so use a message box /SUPPRESSMSGBOXES can suppress. }
  if not Result then SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S: String;
begin
  S := MemoComponentsInfo + NewLine + NewLine;
  S := S + 'Platform:' + NewLine + Space + GetArch('') + NewLine + NewLine;
  S := S + 'IIS:' + NewLine + Space + 'Site: ' + GetSiteName('') + NewLine + Space + 'URL: http(s)://<server>/' + GetBasePath('') + NewLine + NewLine;
  S := S + 'Folders:' + NewLine + Space + 'Apps: ' + GetWebRoot + NewLine + Space + 'Database: ' + GetDatabasePath('') + NewLine;
  if WizardIsComponentSelected('notifications') then S := S + Space + 'Notification logs: ' + GetNotifDir + NewLine;
  S := S + NewLine + 'Database:' + NewLine;
  if DbExists then S := S + Space + 'Keep and upgrade the existing database' + NewLine
  else S := S + Space + 'Create a new database; first Admin account: ' + Trim(AdminPage.Values[0]) + NewLine;
  if GetCertThumbprint <> '' then
    S := S + NewLine + 'HTTPS:' + NewLine + Space + 'Port ' + Trim(HttpsPortEdit.Text) + ', certificate ' + GetCertThumbprint + NewLine;
  S := S + NewLine + 'Prerequisites:' + NewLine;
  if InstallIisRequested then S := S + Space + 'Install IIS' + NewLine;
  if HostingBundleInstalled then S := S + Space + 'ASP.NET Core 10 Hosting Bundle: installed' + NewLine
  else S := S + Space + 'ASP.NET Core 10 Hosting Bundle: will be downloaded and installed' + NewLine;
  Result := S;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax > 0 then ProgressPage.SetProgress(Integer(Progress div 1024), Integer(ProgressMax div 1024));
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Pages: array of Integer;
  I, ResultCode: Integer;
  Lines: TArrayOfString;
  Installer: String;
begin
  Result := '';
  { Silent installs never ran the page logic, so do it here. }
  WizardForm.DirEdit.Text := GetWebRoot;
  RefreshDbState;
  LoadCertificates;
  SetArrayLength(Pages, 8);
  Pages[0] := wpSelectComponents; Pages[1] := IisPage.ID; Pages[2] := FolderPage.ID; Pages[3] := DbPage.ID;
  Pages[4] := AdminPage.ID; Pages[5] := SapProdPage.ID; Pages[6] := SapDevPage.ID; Pages[7] := HttpsPage.ID;
  ProgressPage.Show;
  try
    if InstallIisRequested then
    begin
      ProgressPage.SetText('Installing IIS (this can take several minutes)...', '');
      if not RunHelper('InstallIIS', '', Lines) or not IisInstalled then
      begin
        Result := 'Installing IIS failed. Install the Web Server (IIS) role, then run setup again.';
        Exit;
      end;
    end;

    for I := 0 to GetArrayLength(Pages) - 1 do
      if Result = '' then
        if not ((Pages[I] = AdminPage.ID) and DbExists) then
          if not (((Pages[I] = SapProdPage.ID) or (Pages[I] = SapDevPage.ID)) and not WizardIsComponentSelected('notifications')) then
            if Pages[I] <> DbPage.ID then { the existing-database check needs the runtime; done below }
              Result := ValidatePage(Pages[I]);
    if Result <> '' then Exit;

    if not HostingBundleInstalled then
    begin
      ProgressPage.SetText('Downloading the ASP.NET Core 10 Hosting Bundle...', '{#HostingBundleUrl}');
      try
        DownloadTemporaryFile('{#HostingBundleUrl}', 'dotnet-hosting-win.exe', '', @OnDownloadProgress);
      except
        Result := 'Downloading the ASP.NET Core 10 Hosting Bundle failed: ' + GetExceptionMessage + #13#10
          + 'Install it manually from https://dotnet.microsoft.com/download/dotnet/10.0, then run setup again.';
        Exit;
      end;
      ProgressPage.SetText('Installing the ASP.NET Core 10 Hosting Bundle...', '');
      ProgressPage.SetProgress(0, 0);
      Installer := ExpandConstant('{tmp}\dotnet-hosting-win.exe');
      if not Exec(Installer, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
        or not ((ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1641)) then
      begin
        Result := 'Installing the ASP.NET Core 10 Hosting Bundle failed (exit code ' + IntToStr(ResultCode) + ').';
        Exit;
      end;
      if (ResultCode = 3010) or (ResultCode = 1641) then NeedsRestart := True;
      { Restart IIS so it loads the ASP.NET Core Module. }
      Exec(ExpandConstant('{sys}\net.exe'), 'stop was /y', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\net.exe'), 'start w3svc', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if not HostingBundleInstalled then
      begin
        Result := 'The ASP.NET Core 10 Hosting Bundle is still not detected after installing it.';
        Exit;
      end;
    end;

    ProgressPage.SetText('Preparing the configuration database...', GetDatabasePath(''));
    Result := ValidatePage(DbPage.ID);
    if Result <> '' then Exit;
    case RunSetupTool('configure', True) of
      0: ;
      2: begin Result := 'The database password does not open the existing database ' + GetDatabasePath('') + '.'; Exit; end;
    else
      begin Result := 'Preparing the configuration database failed. See the setup log for details.'; Exit; end;
    end;

    ProgressPage.SetText('Stopping the app pools...', '');
    if IisInstalled then RunSetupIis('-StopAppPools', False);
  finally
    ProgressPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Args: String;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Configuring IIS...';
    Args := '-WebRoot ' + AddQuotes(GetWebRoot) + ' -Arch ' + GetArch('') + ' -DatabasePath ' + AddQuotes(GetDatabasePath(''))
      + ' -NotificationsDataFolder ' + AddQuotes(GetNotifDir);
    if GetCertThumbprint <> '' then
    begin
      Args := Args + ' -CertificateThumbprint ' + GetCertThumbprint + ' -HttpsPort ' + Trim(HttpsPortEdit.Text);
      if Trim(HttpsHostEdit.Text) <> '' then Args := Args + ' -HttpsHostName ' + AddQuotes(Trim(HttpsHostEdit.Text));
    end;
    if not RunSetupIis(Args, True) then
      SuppressibleMsgBox('The files were installed, but configuring IIS failed. See the setup log, fix the problem, '
        + 'then run setup again or run ' + ExpandConstant('{app}\deploy\Setup-IIS.ps1') + '.', mbError, MB_OK, IDOK);
  end;
end;

{ ---------- uninstall ---------- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Site, BasePath, Apps: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKLM, '{#RegKey}', 'SiteName', Site) and RegQueryStringValue(HKLM, '{#RegKey}', 'BasePath', BasePath)
      and RegQueryStringValue(HKLM, '{#RegKey}', 'Apps', Apps) and (Apps <> '') then
    begin
      Exec(PowerShellExe, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File '
        + AddQuotes(ExpandConstant('{app}\deploy\Setup-IIS.ps1')) + ' -Remove -SiteName ' + AddQuotes(Site)
        + ' -BasePath ' + BasePath + ' -Apps ' + Apps, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Log('Removed IIS applications (exit code ' + IntToStr(ResultCode) + '). config.db and the notification logs were kept.');
    end;
end;
