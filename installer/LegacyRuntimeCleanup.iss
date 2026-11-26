// Older self-contained builds installed these Windows Desktop framework translations.
// Remove only retired framework files from an existing panel installation.
var
  UpgradingPanel: Boolean;

procedure RemoveOldRuntimeTranslations(const AppDir: String);
var
  Cultures, Assemblies: TArrayOfString;
  I, J: Integer;
  Folder, FileName: String;
begin
  if (GetFileAttributesW(AppDir) and $400) <> 0 then Exit;
  Cultures := StringSplit('cs,de,es,fr,it,ja,ko,pl,pt-BR,ru,tr', [','], stExcludeEmpty);
  Assemblies := StringSplit(
    'Microsoft.VisualBasic.Forms,PresentationCore,PresentationFramework,PresentationUI,ReachFramework,' +
    'System.Windows.Controls.Ribbon,System.Windows.Forms.Design,System.Windows.Forms.Primitives,' +
    'System.Windows.Forms,System.Windows.Input.Manipulations,System.Xaml,UIAutomationClient,' +
    'UIAutomationClientSideProviders,UIAutomationProvider,UIAutomationTypes,WindowsBase,WindowsFormsIntegration', [','], stExcludeEmpty);
  for I := 0 to GetArrayLength(Cultures) - 1 do begin
    Folder := AddBackslash(AppDir) + Cultures[I];
    if DirExists(Folder) and ((GetFileAttributesW(Folder) and $400) = 0) then begin
      for J := 0 to GetArrayLength(Assemblies) - 1 do begin
        FileName := Folder + '\' + Assemblies[J] + '.resources.dll';
        if FileExists(FileName) and ((GetFileAttributesW(FileName) and $400) = 0) then
          if not DeleteFile(FileName) then Log('Could not remove old framework resource: ' + FileName);
      end;
      // RemoveDir only succeeds if no other files remain.
      RemoveDir(Folder);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir: String;
begin
  AppDir := ExpandConstant('{app}');
  if CurStep = ssInstall then
    UpgradingPanel := FileExists(AppDir + '\DonnerControlPanel.exe') and
      FileExists(AppDir + '\DonnerControlPanel.deps.json');
  if (CurStep = ssPostInstall) and UpgradingPanel then
    RemoveOldRuntimeTranslations(AppDir);
end;
