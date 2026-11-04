// Shared with the isolated installer regression fixture.
#ifndef UserDataDirectory
  #define UserDataDirectory "{localappdata}\DonnerControlPanel"
#endif
#ifndef StartupRegistryKey
  #define StartupRegistryKey "Software\Microsoft\Windows\CurrentVersion\Run"
#endif
#ifndef StartupApprovalRegistryKey
  #define StartupApprovalRegistryKey "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
#endif

var
  PurgeUserData: Boolean;

function GetFileAttributesW(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function InitializeUninstall: Boolean;
var
  Choice: Integer;
begin
  Result := True;
  PurgeUserData := ExpandConstant('{param:PURGEUSERDATA|0}') = '1';
  if ExpandConstant('{param:KEEPUSERDATA|0}') = '1' then begin
    PurgeUserData := False;
    Exit;
  end;
  // Silent management/upgrade scripts retain settings unless explicitly requested.
  if UninstallSilent then Exit;
  Choice := MsgBox('是否同时删除此用户的全部面板配置、预设、备份和日志？' + #13#10#13#10 +
    '是：彻底清理面板数据。删除后无法恢复，请先导出需要的预设。' + #13#10 +
    '否：保留配置，重新安装后继续使用。' + #13#10 +
    '取消：不卸载。' + #13#10#13#10 +
    'Donner 原厂驱动和 Voicemeeter Banana 为独立软件，请在 Windows“已安装的应用”中分别卸载。' + #13#10 +
    '如需恢复修改过的 Windows 设备名称，请先在面板“设备名称”中恢复。',
    mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
  Result := Choice <> IDCANCEL;
  PurgeUserData := Choice = IDYES;
end;

// Walk only ordinary directories. Never follow a junction/symbolic link placed in user data.
function DeletePanelData(const Folder: String): Boolean;
var
  Entry: TFindRec;
  Child: String;
begin
  Result := True;
  if not DirExists(Folder) then Exit;
  if (GetFileAttributesW(Folder) and $400) <> 0 then begin
    Log('Refusing linked data directory: ' + Folder);
    Result := False;
    Exit;
  end;
  if FindFirst(Folder + '\*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Child := Folder + '\' + Entry.Name;
          if (Entry.Attributes and $400) <> 0 then begin
            Log('Retaining linked entry: ' + Child);
            Result := False;
          end else if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
            if not DeletePanelData(Child) then Result := False;
          end else if not DeleteFile(Child) then begin
            Log('Could not delete: ' + Child);
            Result := False;
          end;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
  if not RemoveDir(Folder) then Result := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, OwnPrefix, DataFolder: String;
  ExitCode: Integer;
  OwnStartup: Boolean;
begin
  if CurUninstallStep = usUninstall then begin
    if FileExists(ExpandConstant('{app}\DonnerControlPanel.exe')) then begin
      if not Exec(ExpandConstant('{app}\DonnerControlPanel.exe'), '--shutdown-for-uninstall', '',
        SW_HIDE, ewWaitUntilTerminated, ExitCode) then
        RaiseException('无法退出 Donner 面板。请先从托盘菜单退出面板，再重试卸载。');
      if ExitCode <> 0 then
        RaiseException('Donner 面板仍在保存配置。请正常退出面板后再重试卸载。');
    end;
    OwnPrefix := '"' + ExpandConstant('{app}\DonnerControlPanel.exe') + '"';
    OwnStartup := not RegValueExists(HKCU, '{#StartupRegistryKey}', 'DonnerControlPanel');
    if RegQueryStringValue(HKCU, '{#StartupRegistryKey}', 'DonnerControlPanel', Command) then
      OwnStartup := Pos(Uppercase(OwnPrefix), Uppercase(Command)) = 1;
    if OwnStartup then begin
      RegDeleteValue(HKCU, '{#StartupRegistryKey}', 'DonnerControlPanel');
      RegDeleteValue(HKCU, '{#StartupApprovalRegistryKey}', 'DonnerControlPanel');
    end;
  end;
  if (CurUninstallStep = usPostUninstall) and PurgeUserData then begin
    DataFolder := ExpandConstant('{#UserDataDirectory}');
    if not DeletePanelData(DataFolder) then begin
      Log('Some panel data could not be removed: ' + DataFolder);
      if not UninstallSilent then
        MsgBox('部分数据被占用或包含目录链接，未能删除。请检查：' + #13#10 + DataFolder, mbError, MB_OK);
    end;
  end;
end;
