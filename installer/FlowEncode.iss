#ifndef AppName
  #define AppName "FlowEncode"
#endif
#ifndef AppDisplayName
  #define AppDisplayName "FlowEncode"
#endif
#ifndef AppPublisher
  #define AppPublisher "frankie1024"
#endif
#ifndef AppPublisherUrl
  #define AppPublisherUrl "https://github.com/frankie1024/FlowEncode"
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppVersionInfo
  #define AppVersionInfo "0.0.0.0"
#endif
#ifndef AppExeName
  #define AppExeName "FlowEncode.exe"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef OutputBaseName
  #define OutputBaseName "FlowEncode_Setup_v" + AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "."
#endif
#ifndef WebView2BootstrapperFile
  #define WebView2BootstrapperFile ""
#endif
#ifndef VisualCppRedistUrl
  #define VisualCppRedistUrl "https://aka.ms/vs/17/release/vc_redist.x64.exe"
#endif
#ifndef WindowsAppRuntimeInstallerUrl
  #define WindowsAppRuntimeInstallerUrl "https://download.microsoft.com/download/e28b236e-e201-409d-b045-f303907c5226/WindowsAppRuntimeInstall-x64.exe"
#endif
#ifndef SetupIconFile
  #define SetupIconFile ""
#endif
#ifndef WizardImageFile
  #define WizardImageFile ""
#endif
#ifndef WizardSmallImageFile
  #define WizardSmallImageFile ""
#endif
#define VapourSynthProgId "FlowEncode.VapourSynthScript"
#define VapourSynthFileTypeName "VapourSynth Script"
#define AppId "{{679B3447-8856-4CA5-99F4-291221F43274}"

[Setup]
AppId={#AppId}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherUrl}
AppSupportURL={#AppPublisherUrl}
AppUpdatesURL={#AppPublisherUrl}
DefaultDirName={autopf64}\{#AppName}
UsePreviousAppDir=yes
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardImageFile={#WizardImageFile}
WizardSmallImageFile={#WizardSmallImageFile}
SetupIconFile={#SetupIconFile}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppDisplayName} Setup
VersionInfoProductName={#AppDisplayName}
VersionInfoProductTextVersion={#AppVersion}
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associatevpy"; Description: "Associate .vpy files with {#AppDisplayName}"; GroupDescription: "Additional tasks:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#if WebView2BootstrapperFile != ""
Source: "{#WebView2BootstrapperFile}"; Flags: dontcopy
#endif

[Icons]
Name: "{autoprograms}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.vpy"; ValueType: string; ValueName: ""; ValueData: "{#VapourSynthProgId}"; Tasks: associatevpy; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.vpy"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.vpy\OpenWithProgids"; ValueType: string; ValueName: "{#VapourSynthProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.vpy\OpenWithProgids"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\{#VapourSynthProgId}"; ValueType: string; ValueName: ""; ValueData: "{#VapourSynthFileTypeName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#VapourSynthProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#VapourSynthProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppDisplayName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".vpy"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppDisplayName}}"; Flags: nowait postinstall skipifsilent; Check: ShouldLaunchApplication

[Code]
const
  PrerequisiteCount = 3;
  PrerequisiteVisualCpp = 0;
  PrerequisiteWindowsAppRuntime = 1;
  PrerequisiteWebView2 = 2;

  PrerequisiteStateInstalled = 0;
  PrerequisiteStateMissing = 1;
  PrerequisiteStateUnknown = 2;

  PrerequisiteActionInstall = 0;
  PrerequisiteActionSkip = 1;

  VisualCppRedistUrl = '{#VisualCppRedistUrl}';
  VisualCppRedistName = 'vc_redist.x64.exe';
  WindowsAppRuntimeInstallerUrl = '{#WindowsAppRuntimeInstallerUrl}';
  WindowsAppRuntimeInstallerName = 'WindowsAppRuntimeInstall-x64.exe';
  WebView2RuntimeClientGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2BootstrapperName = 'MicrosoftEdgeWebView2Setup.exe';

var
  PrerequisitePage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  InstallProgressPage: TOutputMarqueeProgressWizardPage;
  PrerequisiteStates: array[0..PrerequisiteCount - 1] of Integer;
  PrerequisiteVersions: array[0..PrerequisiteCount - 1] of string;
  AllowApplicationLaunch: Boolean;

function GetWebView2MachineClientKeyPath(): string;
begin
  if IsWin64 then
    Result := 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientGuid
  else
    Result := 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientGuid;
end;

function GetWebView2UserClientKeyPath(): string;
begin
  Result := 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2RuntimeClientGuid;
end;

function ReadInstalledWebView2Version(const RootKey: Integer; const SubKeyName: string): string;
begin
  if not RegQueryStringValue(RootKey, SubKeyName, 'pv', Result) then
    Result := '';
end;

function IsValidInstalledWebView2Version(const VersionText: string): Boolean;
begin
  Result :=
    (Trim(VersionText) <> '') and
    (CompareText(Trim(VersionText), '0.0.0.0') <> 0);
end;

function GetPrerequisiteName(const Index: Integer): string;
begin
  case Index of
    PrerequisiteVisualCpp:
      Result := 'Microsoft Visual C++ Redistributable x64';
    PrerequisiteWindowsAppRuntime:
      Result := 'Windows App Runtime 1.8 x64';
    PrerequisiteWebView2:
      Result := 'Microsoft Edge WebView2 Runtime';
  else
    Result := 'Unknown prerequisite';
  end;
end;

function GetPrerequisiteDescription(const Index: Integer): string;
begin
  case Index of
    PrerequisiteVisualCpp:
      Result := 'Required for the WinUI desktop app runtime.';
    PrerequisiteWindowsAppRuntime:
      Result := 'Required for the unpackaged Windows App SDK host.';
    PrerequisiteWebView2:
      Result := 'Required for the integrated VapourSynth editor surface.';
  else
    Result := '';
  end;
end;

function IsLaunchCriticalPrerequisite(const Index: Integer): Boolean;
begin
  Result :=
    (Index = PrerequisiteVisualCpp) or
    (Index = PrerequisiteWindowsAppRuntime);
end;

function GetPrerequisiteImpactText(const Index: Integer): string;
begin
  if IsLaunchCriticalPrerequisite(Index) then
    Result := 'FlowEncode may not start without it.'
  else
    Result := 'Related features may not work without it.';
end;

function GetPrerequisiteInstallerName(const Index: Integer): string;
begin
  case Index of
    PrerequisiteVisualCpp:
      Result := VisualCppRedistName;
    PrerequisiteWindowsAppRuntime:
      Result := WindowsAppRuntimeInstallerName;
    PrerequisiteWebView2:
      Result := WebView2BootstrapperName;
  else
    Result := '';
  end;
end;

function GetPrerequisiteDownloadUrl(const Index: Integer): string;
begin
  case Index of
    PrerequisiteVisualCpp:
      Result := VisualCppRedistUrl;
    PrerequisiteWindowsAppRuntime:
      Result := WindowsAppRuntimeInstallerUrl;
  else
    Result := '';
  end;
end;

function GetPrerequisiteInstallParameters(const Index: Integer): string;
begin
  case Index of
    PrerequisiteVisualCpp:
      Result := '/install /passive /norestart';
    PrerequisiteWindowsAppRuntime:
      Result := '';
    PrerequisiteWebView2:
      Result := '/install';
  else
    Result := '';
  end;
end;

function GetPrerequisiteRetryMessage(const Index: Integer; const Detail: string): string;
begin
  Result :=
    GetPrerequisiteName(Index) + ' did not finish installing.' + #13#10#13#10 +
    GetPrerequisiteImpactText(Index);

  if Trim(Detail) <> '' then
  begin
    Result := Result + #13#10#13#10 + 'Details:' + #13#10 + Detail;
  end;

  Result := Result + #13#10#13#10 + 'Retry installation, skip it, or cancel Setup?';
end;

function GetPowerShellPath(): string;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function JoinCapturedOutput(const Lines: TArrayOfString): string;
var
  Index: Integer;
begin
  Result := '';
  for Index := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Result <> '' then
      Result := Result + #13#10;
    Result := Result + Trim(Lines[Index]);
  end;
end;

function ExecutePowerShellScriptAndCapture(
  const ScriptBody: string;
  var ResultCode: Integer;
  var StdOutText: string;
  var StdErrText: string): Boolean;
var
  ScriptPath: string;
  Output: TExecOutput;
  ScriptLines: TArrayOfString;
begin
  ResultCode := -1;
  StdOutText := '';
  StdErrText := '';

  ScriptPath := ExpandConstant('{tmp}\flowencode_prereq_check.ps1');
  SetArrayLength(ScriptLines, 1);
  ScriptLines[0] := ScriptBody;
  SaveStringsToUTF8File(ScriptPath, ScriptLines, False);

  try
    Result := ExecAndCaptureOutput(
      GetPowerShellPath(),
      '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(ScriptPath),
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode,
      Output);
  except
    StdErrText := GetExceptionMessage;
    Log('PowerShell detection failed to launch: ' + StdErrText);
    Result := False;
    exit;
  end;

  StdOutText := Trim(JoinCapturedOutput(Output.StdOut));
  StdErrText := Trim(JoinCapturedOutput(Output.StdErr));
end;

function VerifyMicrosoftSignedInstaller(const InstallerPath: string; var FailureDetail: string): Boolean;
var
  ResultCode: Integer;
  StdOutText: string;
  StdErrText: string;
  ScriptBody: string;
begin
  FailureDetail := '';
  ScriptBody :=
    '$path = @''' + #13#10 +
    InstallerPath + #13#10 +
    '''@' + #13#10 +
    '$signature = Get-AuthenticodeSignature -LiteralPath $path' + #13#10 +
    'if ($null -eq $signature) { Write-Error ''The file does not have an Authenticode signature.''; exit 1 }' + #13#10 +
    'if ($signature.Status -ne ''Valid'') { Write-Error (''Signature status: '' + $signature.Status + ''. '' + $signature.StatusMessage); exit 1 }' + #13#10 +
    '$certificate = $signature.SignerCertificate' + #13#10 +
    'if ($null -eq $certificate) { Write-Error ''The file does not have a signer certificate.''; exit 1 }' + #13#10 +
    '$subject = $certificate.Subject' + #13#10 +
    'if (($subject -notlike ''*O=Microsoft Corporation*'') -and ($subject -notlike ''*CN=Microsoft Corporation*'')) { Write-Error (''Unexpected signer: '' + $subject); exit 1 }' + #13#10 +
    'Write-Output (''Verified signer: '' + $subject)';

  if not ExecutePowerShellScriptAndCapture(ScriptBody, ResultCode, StdOutText, StdErrText) then
  begin
    FailureDetail := 'Signature verification could not be started.';
    if StdErrText <> '' then
      FailureDetail := FailureDetail + ' ' + StdErrText;
    Result := False;
    exit;
  end;

  if ResultCode <> 0 then
  begin
    FailureDetail := 'Signature verification failed.';
    if StdErrText <> '' then
      FailureDetail := FailureDetail + ' ' + StdErrText
    else if StdOutText <> '' then
      FailureDetail := FailureDetail + ' ' + StdOutText;
    Result := False;
    exit;
  end;

  if StdOutText <> '' then
    Log(StdOutText);
  Result := True;
end;

function TryDetectWindowsAppRuntimeVersion(var VersionText: string; var FailureDetail: string): Boolean;
var
  ResultCode: Integer;
  StdOutText: string;
  StdErrText: string;
  ScriptBody: string;
begin
  VersionText := '';
  FailureDetail := '';

  ScriptBody :=
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    '$pkg = Get-AppxPackage Microsoft.WindowsAppRuntime.1.8 | Where-Object { $_.Architecture -eq ''X64'' } | Sort-Object Version -Descending | Select-Object -First 1' + #13#10 +
    'if ($null -ne $pkg) { [Console]::Out.WriteLine($pkg.Version.ToString()) }';

  if not ExecutePowerShellScriptAndCapture(ScriptBody, ResultCode, StdOutText, StdErrText) then
  begin
    FailureDetail := StdErrText;
    Result := False;
    exit;
  end;

  if ResultCode <> 0 then
  begin
    FailureDetail := StdErrText;
    if FailureDetail = '' then
      FailureDetail := 'PowerShell exited with code ' + IntToStr(ResultCode) + '.';
    Result := False;
    exit;
  end;

  VersionText := Trim(StdOutText);
  Result := True;
end;

procedure DetectVisualCppPrerequisite;
var
  InstalledFlag: Cardinal;
  VersionText: string;
  KeyPath: string;
begin
  KeyPath := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64';
  InstalledFlag := 0;
  VersionText := '';

  if RegQueryDWordValue(HKLM, KeyPath, 'Installed', InstalledFlag)
    and (InstalledFlag <> 0)
    and RegQueryStringValue(HKLM, KeyPath, 'Version', VersionText)
    and (Trim(VersionText) <> '') then
  begin
    PrerequisiteStates[PrerequisiteVisualCpp] := PrerequisiteStateInstalled;
    PrerequisiteVersions[PrerequisiteVisualCpp] := Trim(VersionText);
    Log('Detected Visual C++ Redistributable x64: ' + PrerequisiteVersions[PrerequisiteVisualCpp]);
  end
  else
  begin
    PrerequisiteStates[PrerequisiteVisualCpp] := PrerequisiteStateMissing;
    PrerequisiteVersions[PrerequisiteVisualCpp] := '';
    Log('Visual C++ Redistributable x64 was not detected.');
  end;
end;

procedure DetectWindowsAppRuntimePrerequisite;
var
  VersionText: string;
  FailureDetail: string;
begin
  if TryDetectWindowsAppRuntimeVersion(VersionText, FailureDetail) then
  begin
    if VersionText <> '' then
    begin
      PrerequisiteStates[PrerequisiteWindowsAppRuntime] := PrerequisiteStateInstalled;
      PrerequisiteVersions[PrerequisiteWindowsAppRuntime] := VersionText;
      Log('Detected Windows App Runtime 1.8 x64: ' + VersionText);
    end
    else
    begin
      PrerequisiteStates[PrerequisiteWindowsAppRuntime] := PrerequisiteStateMissing;
      PrerequisiteVersions[PrerequisiteWindowsAppRuntime] := '';
      Log('Windows App Runtime 1.8 x64 was not detected.');
    end;
  end
  else
  begin
    PrerequisiteStates[PrerequisiteWindowsAppRuntime] := PrerequisiteStateUnknown;
    PrerequisiteVersions[PrerequisiteWindowsAppRuntime] := '';
    if FailureDetail <> '' then
      Log('Windows App Runtime 1.8 x64 detection failed: ' + FailureDetail)
    else
      Log('Windows App Runtime 1.8 x64 detection failed.');
  end;
end;

procedure DetectWebView2Prerequisite;
var
  InstalledVersion: string;
begin
  InstalledVersion := ReadInstalledWebView2Version(HKLM, GetWebView2MachineClientKeyPath());
  if IsValidInstalledWebView2Version(InstalledVersion) then
  begin
    PrerequisiteStates[PrerequisiteWebView2] := PrerequisiteStateInstalled;
    PrerequisiteVersions[PrerequisiteWebView2] := Trim(InstalledVersion);
    Log('Detected per-machine WebView2 Runtime: ' + PrerequisiteVersions[PrerequisiteWebView2]);
    exit;
  end;

  InstalledVersion := ReadInstalledWebView2Version(HKCU, GetWebView2UserClientKeyPath());
  if IsValidInstalledWebView2Version(InstalledVersion) then
  begin
    PrerequisiteStates[PrerequisiteWebView2] := PrerequisiteStateInstalled;
    PrerequisiteVersions[PrerequisiteWebView2] := Trim(InstalledVersion);
    Log('Detected per-user WebView2 Runtime: ' + PrerequisiteVersions[PrerequisiteWebView2]);
  end
  else
  begin
    PrerequisiteStates[PrerequisiteWebView2] := PrerequisiteStateMissing;
    PrerequisiteVersions[PrerequisiteWebView2] := '';
    Log('WebView2 Runtime was not detected.');
  end;
end;

procedure DetectSinglePrerequisite(const Index: Integer);
begin
  case Index of
    PrerequisiteVisualCpp:
      DetectVisualCppPrerequisite;
    PrerequisiteWindowsAppRuntime:
      DetectWindowsAppRuntimePrerequisite;
    PrerequisiteWebView2:
      DetectWebView2Prerequisite;
  end;
end;

procedure DetectPrerequisites;
begin
  DetectSinglePrerequisite(PrerequisiteVisualCpp);
  DetectSinglePrerequisite(PrerequisiteWindowsAppRuntime);
  DetectSinglePrerequisite(PrerequisiteWebView2);
  AllowApplicationLaunch :=
    (PrerequisiteStates[PrerequisiteVisualCpp] = PrerequisiteStateInstalled) and
    (PrerequisiteStates[PrerequisiteWindowsAppRuntime] = PrerequisiteStateInstalled);
end;

function BuildPrerequisiteStatusLine(const Index: Integer): string;
begin
  case PrerequisiteStates[Index] of
    PrerequisiteStateInstalled:
      begin
        Result := '- ' + GetPrerequisiteName(Index) + ': installed';
        if Trim(PrerequisiteVersions[Index]) <> '' then
          Result := Result + ' (' + Trim(PrerequisiteVersions[Index]) + ')';
      end;
    PrerequisiteStateMissing:
      Result := '- ' + GetPrerequisiteName(Index) + ': missing';
  else
    Result := '- ' + GetPrerequisiteName(Index) + ': detection failed';
  end;

  Result := Result + #13#10 + '  ' + GetPrerequisiteDescription(Index) + ' ' + GetPrerequisiteImpactText(Index);
end;

function BuildPrerequisiteSummaryText(): string;
begin
  Result :=
    'Setup checked the Windows runtime components used by FlowEncode.' + #13#10#13#10 +
    BuildPrerequisiteStatusLine(PrerequisiteVisualCpp) + #13#10#13#10 +
    BuildPrerequisiteStatusLine(PrerequisiteWindowsAppRuntime) + #13#10#13#10 +
    BuildPrerequisiteStatusLine(PrerequisiteWebView2) + #13#10#13#10 +
    'Choose how Setup should continue:' + #13#10 +
    '- Install missing prerequisites now: recommended.' + #13#10 +
    '- Skip prerequisite installation: Setup will continue, but the app may not start or some features may be unavailable.' + #13#10#13#10 +
    'Setup never reinstalls a prerequisite that was already detected.';
end;

function GetPendingPrerequisiteCount(): Integer;
var
  Index: Integer;
begin
  Result := 0;
  for Index := 0 to PrerequisiteCount - 1 do
  begin
    if PrerequisiteStates[Index] <> PrerequisiteStateInstalled then
      Result := Result + 1;
  end;
end;

function HasConfirmedMissingPrerequisites(): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to PrerequisiteCount - 1 do
  begin
    if PrerequisiteStates[Index] = PrerequisiteStateMissing then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function HasRemainingCriticalPrerequisites(): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to PrerequisiteCount - 1 do
  begin
    if IsLaunchCriticalPrerequisite(Index)
      and (PrerequisiteStates[Index] <> PrerequisiteStateInstalled) then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function BuildContinueWarningMessage(): string;
begin
  Result := 'Some prerequisites are still unavailable.' + #13#10#13#10;

  if HasRemainingCriticalPrerequisites() then
    Result := Result + 'FlowEncode may not start until the launch-critical runtimes are installed.'
  else
    Result := Result + 'FlowEncode can still be installed, but some features may remain unavailable.';

  Result := Result + #13#10#13#10 + 'Continue with the main installation anyway?';
end;

procedure RefreshPrerequisitePageContent;
begin
  if Assigned(PrerequisitePage) then
    PrerequisitePage.SubCaptionLabel.Caption := BuildPrerequisiteSummaryText();
end;

function DownloadPrerequisiteInstaller(const Index: Integer; var InstallerPath: string; var FailureDetail: string): Boolean;
var
  Url: string;
  BaseName: string;
begin
  InstallerPath := '';
  FailureDetail := '';

  if Index = PrerequisiteWebView2 then
  begin
#if WebView2BootstrapperFile == ""
    FailureDetail := 'The WebView2 bootstrapper was not compiled into this Setup build.';
    Result := False;
    exit;
#else
    ExtractTemporaryFile(WebView2BootstrapperName);
    InstallerPath := ExpandConstant('{tmp}\') + WebView2BootstrapperName;
    Result := FileExists(InstallerPath);
    if not Result then
      FailureDetail := 'The embedded WebView2 bootstrapper could not be extracted.';
    if Result and not VerifyMicrosoftSignedInstaller(InstallerPath, FailureDetail) then
      Result := False;
    exit;
#endif
  end;

  Url := GetPrerequisiteDownloadUrl(Index);
  BaseName := GetPrerequisiteInstallerName(Index);
  InstallerPath := ExpandConstant('{tmp}\') + BaseName;

  if Url = '' then
  begin
    FailureDetail := 'No download URL is configured for this prerequisite.';
    Result := False;
    exit;
  end;

  try
    DownloadPage.Clear;
    DownloadPage.SetText(
      'Downloading ' + GetPrerequisiteName(Index),
      'Setup is downloading the Microsoft installer required for this prerequisite.');
    DownloadPage.Add(Url, BaseName, '');
    DownloadPage.Show;
    try
      DownloadPage.Download;
    finally
      DownloadPage.Hide;
    end;
  except
    FailureDetail := GetExceptionMessage;
    Result := False;
    exit;
  end;

  Result := FileExists(InstallerPath);
  if not Result then
  begin
    FailureDetail := 'The prerequisite installer download did not produce a file.';
    exit;
  end;

  Result := VerifyMicrosoftSignedInstaller(InstallerPath, FailureDetail);
end;

function IsPrerequisiteInstallerSuccessCode(const ResultCode: Integer): Boolean;
begin
  Result := (ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1641);
end;

function IsPrerequisiteInstallerRebootSuccessCode(const ResultCode: Integer): Boolean;
begin
  Result := (ResultCode = 3010) or (ResultCode = 1641);
end;

function LaunchPrerequisiteInstaller(
  const Index: Integer;
  const InstallerPath: string;
  var FailureDetail: string;
  var RebootRequired: Boolean): Boolean;
var
  ResultCode: Integer;
begin
  FailureDetail := '';
  RebootRequired := False;
  InstallProgressPage.SetText(
    'Installing ' + GetPrerequisiteName(Index),
    'A Microsoft installer window may appear. Finish that installation to continue with FlowEncode Setup.');

  InstallProgressPage.Show;
  try
    Result := Exec(
      InstallerPath,
      GetPrerequisiteInstallParameters(Index),
      '',
      SW_SHOWNORMAL,
      ewWaitUntilTerminated,
      ResultCode);
  finally
    InstallProgressPage.Hide;
  end;

  if not Result then
  begin
    FailureDetail := 'Setup could not launch the prerequisite installer.';
    exit;
  end;

  Log(GetPrerequisiteName(Index) + ' installer exit code: ' + IntToStr(ResultCode));
  RebootRequired := IsPrerequisiteInstallerRebootSuccessCode(ResultCode);

  if not IsPrerequisiteInstallerSuccessCode(ResultCode) then
  begin
    DetectSinglePrerequisite(Index);
    if PrerequisiteStates[Index] = PrerequisiteStateInstalled then
    begin
      Log(GetPrerequisiteName(Index) + ' was detected after installer exit code ' + IntToStr(ResultCode) + '.');
      Result := True;
      exit;
    end;

    FailureDetail := 'The prerequisite installer exited with code ' + IntToStr(ResultCode) + '.';
    Result := False;
  end;
end;

function WaitForPrerequisiteInstallation(const Index: Integer): Boolean;
var
  Attempt: Integer;
begin
  for Attempt := 0 to 29 do
  begin
    DetectSinglePrerequisite(Index);
    if PrerequisiteStates[Index] = PrerequisiteStateInstalled then
    begin
      Result := True;
      exit;
    end;

    Sleep(1000);
  end;

  Result := False;
end;

function PromptForPrerequisiteRetry(const Index: Integer; const Detail: string): Integer;
begin
  Result := MsgBox(
    GetPrerequisiteRetryMessage(Index, Detail),
    mbError,
    MB_ABORTRETRYIGNORE);
end;

function InstallSinglePrerequisite(const Index: Integer): Boolean;
var
  InstallerPath: string;
  FailureDetail: string;
  RebootRequired: Boolean;
  RetryChoice: Integer;
begin
  while True do
  begin
    if not DownloadPrerequisiteInstaller(Index, InstallerPath, FailureDetail) then
    begin
      RetryChoice := PromptForPrerequisiteRetry(Index, FailureDetail);
      if RetryChoice = IDRETRY then
        continue;
      Result := RetryChoice = IDIGNORE;
      exit;
    end;

    if LaunchPrerequisiteInstaller(Index, InstallerPath, FailureDetail, RebootRequired) then
    begin
      if WaitForPrerequisiteInstallation(Index) then
      begin
        Result := True;
        exit;
      end;

      if RebootRequired then
      begin
        Log(GetPrerequisiteName(Index) + ' installer requested a restart. Continuing setup.');
        Result := True;
        exit;
      end;

      FailureDetail := 'Setup could not verify that the prerequisite was installed.';
    end;

    DetectSinglePrerequisite(Index);
    RetryChoice := PromptForPrerequisiteRetry(Index, FailureDetail);
    if RetryChoice = IDRETRY then
      continue;

    Result := RetryChoice = IDIGNORE;
    exit;
  end;
end;

function InstallMissingPrerequisites(): Boolean;
var
  Index: Integer;
begin
  Result := False;

  for Index := 0 to PrerequisiteCount - 1 do
  begin
    if PrerequisiteStates[Index] <> PrerequisiteStateMissing then
      continue;

    if not InstallSinglePrerequisite(Index) then
      exit;
  end;

  DetectPrerequisites;
  Result := True;
end;

function ConfirmContinueWithoutPrerequisites(): Boolean;
begin
  Result := MsgBox(
    BuildContinueWarningMessage(),
    mbConfirmation,
    MB_YESNO) = IDYES;
end;

function HandlePrerequisitePageAdvance(): Boolean;
begin
  DetectPrerequisites;
  RefreshPrerequisitePageContent;

  if GetPendingPrerequisiteCount() = 0 then
  begin
    Result := True;
    exit;
  end;

  if PrerequisitePage.SelectedValueIndex = PrerequisiteActionSkip then
  begin
    Result := ConfirmContinueWithoutPrerequisites();
    exit;
  end;

  if not HasConfirmedMissingPrerequisites() then
  begin
    Result := ConfirmContinueWithoutPrerequisites();
    exit;
  end;

  if not InstallMissingPrerequisites() then
  begin
    Result := False;
    exit;
  end;

  RefreshPrerequisitePageContent;

  if GetPendingPrerequisiteCount() = 0 then
  begin
    Result := True;
    exit;
  end;

  Result := ConfirmContinueWithoutPrerequisites();
end;

procedure InitializePrerequisitePage;
begin
  PrerequisitePage := CreateInputOptionPage(
    wpWelcome,
    'Runtime Prerequisites',
    'Review the Windows components used by FlowEncode before the main files are installed.',
    '',
    True,
    False);
  PrerequisitePage.Add('Install missing prerequisites now (Recommended)');
  PrerequisitePage.Add('Skip prerequisite installation and continue');
  PrerequisitePage.SelectedValueIndex := PrerequisiteActionInstall;
  RefreshPrerequisitePageContent;
end;

procedure InitializeWizard;
begin
  DetectPrerequisites;
  InitializePrerequisitePage;
  DownloadPage := CreateDownloadPage(
    'Downloading prerequisites',
    'Setup is downloading the Microsoft prerequisite installer.',
    nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  InstallProgressPage := CreateOutputMarqueeProgressPage(
    'Installing prerequisites',
    'Setup is waiting for the Microsoft prerequisite installer to finish.');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  if Assigned(PrerequisitePage) and (PageID = PrerequisitePage.ID) then
    Result := WizardSilent or (GetPendingPrerequisiteCount() = 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if Assigned(PrerequisitePage) and (CurPageID = PrerequisitePage.ID) then
    Result := HandlePrerequisitePageAdvance();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if Assigned(PrerequisitePage) and (CurPageID = PrerequisitePage.ID) then
  begin
    DetectPrerequisites;
    RefreshPrerequisitePageContent;
  end;
end;

function ShouldLaunchApplication: Boolean;
begin
  Result := AllowApplicationLaunch;
end;
