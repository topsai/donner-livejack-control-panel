#ifndef AppFiles
  #define AppFiles "..\dist\DonnerControlPanel"
#endif
#ifndef DriverInstaller
  #error DriverInstaller must point to the original signed Donner 5.0.0 setup
#endif
#ifndef SetupOutput
  #define SetupOutput "..\dist"
#endif

[Setup]
AppId={{F92AFC50-1F66-4ECF-A3E9-4F489B2086A8}
AppName=Donner 中文控制面板
AppVersion=1.20
AppPublisher=topsai (范斯特洛夫斯基)
AppPublisherURL=https://github.com/topsai/donner-livejack-control-panel
DefaultDirName={localappdata}\Programs\DonnerControlPanel
DefaultGroupName=Donner 中文控制面板
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
DisableWelcomePage=no
WizardStyle=modern
SetupIconFile=..\src\Donner.Panel\Assets\donner.ico
UninstallDisplayIcon={app}\DonnerControlPanel.exe
OutputDir={#SetupOutput}
OutputBaseFilename=DonnerControlPanel-v1.20-Setup-win-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Messages]
WelcomeLabel2=安装 Donner 中文控制面板和所需的原厂 USB 音频驱动。%n%n安装包已包含驱动，无需另行下载。已安装兼容驱动时将自动跳过；缺少驱动时会打开原厂安装向导，请按提示完成。%n%n仅驱动安装需要 Windows 管理员授权。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: checkedonce

[Files]
Source: "{#DriverInstaller}"; DestName: "DonnerDriverSetup.exe"; Flags: dontcopy
Source: "{#AppFiles}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Donner 中文控制面板"; Filename: "{app}\DonnerControlPanel.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Donner 中文控制面板"; Filename: "{app}\DonnerControlPanel.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DonnerControlPanel.exe"; Description: "打开 Donner 中文控制面板"; Flags: postinstall nowait skipifsilent

[Code]
const
  ApiHash = '0a173773d4f5e77e69bb7863158533d3110f78b50972a56e29e1caba679b5753';
  DriverHash = '12ef3f0f517b2f00a7ebba3dbc0cac978e4a71ece8599b31ffe853304dd147f0';
  DriverUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Software_Donner_Donner_UsbAudio_Driver_Setup';
  ApiClassKey = 'SOFTWARE\Classes\CLSID\{5FC1A528-B0CB-4ED9-AD02-50502054ED59}\InprocServer32';
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function CompatibleApi(const FileName: String): Boolean;
var
  Config: AnsiString;
  Folder: String;
begin
  Result := False;
  try
    if not FileExists(FileName) then Exit;
    if CompareText(GetSHA256OfFile(FileName), ApiHash) <> 0 then Exit;
    Folder := ExtractFileDir(FileName);
    if not FileExists(Folder + '\DonnerUsbAudio.sys') then Exit;
    if not LoadStringFromFile(Folder + '\custom.ini', Config) then Exit;
    Result := Pos('8EBCE255-AA2D-4CD5-A142-5C8683F6905D', Uppercase(String(Config))) > 0;
    if Result then Log('Compatible Donner driver: ' + FileName);
  except
    Log('Cannot validate driver: ' + GetExceptionMessage);
  end;
end;

function DriverInFolder(const Folder: String): Boolean;
begin
  Result := CompatibleApi(Folder + '\DonnerUsbAudioapi_x64.dll') or
    CompatibleApi(Folder + '\W10_x64\DonnerUsbAudioapi_x64.dll') or
    CompatibleApi(Folder + '\x64\DonnerUsbAudioapi_x64.dll');
end;

function DriverInRegistry(const Root: Integer): Boolean;
var
  Folder, ApiPath: String;
begin
  Result := False;
  if RegQueryStringValue(Root, DriverUninstallKey, 'InstallLocation', Folder) then
    Result := DriverInFolder(Folder);
  if (not Result) and RegQueryStringValue(Root, ApiClassKey, '', ApiPath) then
    Result := CompatibleApi(RemoveQuotes(ApiPath));
end;

function CompatibleDriverInstalled: Boolean;
begin
  Result := DriverInRegistry(HKLM64) or DriverInRegistry(HKLM32);
end;

procedure AppendDriverSummary;
begin
  if CompatibleDriverInstalled then
    WizardForm.ReadyMemo.Lines.Add(#13#10'原厂驱动：已安装兼容版本，将跳过驱动安装。')
  else
    WizardForm.ReadyMemo.Lines.Add(#13#10'原厂驱动：将先打开内置的原厂驱动安装向导，请按提示完成。');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then AppendDriverSummary;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  FileName: String;
  ErrorCode: Integer;
begin
  Result := '';
  if CompatibleDriverInstalled then begin
    Log('Driver already compatible; skipping original driver setup.');
    Exit;
  end;
  WizardForm.PreparingLabel.Caption := '正在安装原厂驱动，请完成弹出的驱动安装向导…';
  ExtractTemporaryFile('DonnerDriverSetup.exe');
  FileName := ExpandConstant('{tmp}\DonnerDriverSetup.exe');
  if CompareText(GetSHA256OfFile(FileName), DriverHash) <> 0 then begin
    Result := '内置驱动安装包校验失败，请重新下载安装包。';
    Exit;
  end;
  Log('Opening original signed Donner driver setup.');
  if not ShellExec('runas', FileName, '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ErrorCode) then begin
    Result := '无法启动原厂驱动安装：' + SysErrorMessage(ErrorCode) + #13#10'请允许管理员授权，然后重试。';
    Exit;
  end;
  if not CompatibleDriverInstalled then
    Result := '原厂驱动尚未安装完成。请在驱动向导中完成安装，再点击重试。若驱动要求重启，请重启后再次运行本安装包。'
  else
    Log('Original driver installed and verified.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, OwnPrefix: String;
begin
  if CurUninstallStep = usUninstall then begin
    OwnPrefix := '"' + ExpandConstant('{app}\DonnerControlPanel.exe') + '"';
    if RegQueryStringValue(HKCU, StartupKey, 'DonnerControlPanel', Command) then
      if Pos(Uppercase(OwnPrefix), Uppercase(Command)) = 1 then
        RegDeleteValue(HKCU, StartupKey, 'DonnerControlPanel');
  end;
end;
