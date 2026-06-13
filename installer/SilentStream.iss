; SilentStream Inno Setup 스크립트 (계획서 §3.11 / Phase 6)
; 빌드 전 준비:
;   1) dotnet publish src/SilentStream.App -c Release -r win-x64 --self-contained false -o installer\publish
;   2) FFmpeg 정적 빌드(ffmpeg.exe)를 installer\ffmpeg\ 에 배치 (커밋 금지 — .gitignore 적용)
;   3) ISCC.exe installer\SilentStream.iss

#define MyAppName "SilentStream"
#define MyAppVersion "0.1.0"
#define MyAppExeName "SilentStream.exe"

[Setup]
AppId={{8B4A2C71-3F45-4E2A-9D7E-5C1B0A9E6F23}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=output
OutputBaseFilename=SilentStream-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
; 동의 화면: EULA 동의 없이는 설치 불가 (계획서 §1.1)
LicenseFile=EULA_ko.txt
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
; 앱 본체 (dotnet publish 산출물)
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; FFmpeg 번들 (계획서 §3.11)
Source: "ffmpeg\ffmpeg.exe"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName} 제어판"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} 제거"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "지금 SilentStream 시작"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 작업 스케줄러 자동 시작 해제 (등록돼 있을 때만)
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""SilentStream AutoStart"" /F"; \
    Flags: runhidden skipifdoesntexist; RunOnceId: "RemoveSchedTask"

[Registry]
; 시작 프로그램 자동 시작 해제 (제거 시 값 삭제)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueName: "SilentStream"; ValueType: none; Flags: deletevalue uninsdeletevalue

[UninstallDelete]
; 로그는 정리, 녹화 파일과 설정(토큰)은 사용자 자산이므로 보존
Type: filesandordirs; Name: "{userappdata}\SilentStream\logs"

[Code]
var
  AutoStartPage: TInputOptionWizardPage;
  RecordingDirPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  { 자동 시작 방식 선택 (계획서 §3.1: 설치 시 선택) }
  AutoStartPage := CreateInputOptionPage(wpSelectDir,
    '자동 시작 방식', 'PC를 켤 때 SilentStream을 어떻게 시작할까요?',
    '선택한 방식으로 부팅 시 자동 송출·녹화가 시작됩니다.', True, False);
  AutoStartPage.Add('시작 프로그램 (로그인 후 실행, 권장)');
  AutoStartPage.Add('작업 스케줄러 (최고 권한, 로그인 직후 실행)');
  AutoStartPage.SelectedValueIndex := 0;

  { 녹화 폴더 선택 (계획서 §3.6) }
  RecordingDirPage := CreateInputDirPage(AutoStartPage.ID,
    '녹화 폴더', '백업 녹화 파일을 어디에 저장할까요?',
    '용량이 충분한 드라이브를 권장합니다 (기본 한도 100GB, 7일 보관).',
    False, '');
  RecordingDirPage.Add('');
  RecordingDirPage.Values[0] := ExpandConstant('{userdocs}\..\Videos\SilentStream');
end;

function JsonEscape(const S: string): string;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    if S[I] = '\' then
      Result := Result + '\\'
    else if S[I] = '"' then
      Result := Result + '\"'
    else
      Result := Result + S[I];
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigDir, ConfigFile, Autostart, Json: string;
begin
  if CurStep = ssPostInstall then
  begin
    { 설치 선택을 초기 config.json 으로 기록 (이미 있으면 보존) }
    ConfigDir := ExpandConstant('{userappdata}\SilentStream');
    ConfigFile := ConfigDir + '\config.json';
    if not FileExists(ConfigFile) then
    begin
      ForceDirectories(ConfigDir);
      if AutoStartPage.SelectedValueIndex = 1 then
        Autostart := 'scheduler'
      else
        Autostart := 'startup';
      Json :=
        '{' + #13#10 +
        '  "version": 1,' + #13#10 +
        '  "recording": { "enabled": true, "folder": "' +
             JsonEscape(RecordingDirPage.Values[0]) + '",' + #13#10 +
        '                 "maxSizeGb": 100, "retentionDays": 7, "minFreeGb": 5 },' + #13#10 +
        '  "autostart": "' + Autostart + '"' + #13#10 +
        '}' + #13#10;
      SaveStringToFile(ConfigFile, Json, False);
    end;
  end;
end;
