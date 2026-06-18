; SilentStream Inno Setup 스크립트 (계획서 §3.11 / Phase 6)
; 빌드 전 준비 (self-contained: 대상 PC에 .NET/ASP.NET Core 런타임 불필요):
;   1) dotnet publish src/SilentStream.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o installer\publish
;   2) FFmpeg 정적 빌드(ffmpeg.exe)를 installer\ffmpeg\ 에 배치 (커밋 금지 — .gitignore 적용)
;   3) OAuth 데스크톱 시크릿을 installer\secrets\client_secret.json 에 배치 (커밋 금지 — .gitignore 적용)
;   4) ISCC.exe installer\SilentStream.iss

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
; OAuth 데스크톱 클라이언트 시크릿 번들 — 대상 PC의 %AppData%\SilentStream\ 에 자동 배치(이미 있으면 보존).
; 데스크톱 OAuth client는 기밀이 아니며(설치본은 gitignore), 사용자의 수동 복사 단계를 없애기 위함.
; 설치 후 사용자가 할 일은 최초 1회 브라우저 OAuth 로그인뿐(토큰은 기기+계정 종속이라 사전 굽기 불가).
; skipifsourcedoesntexist: 시크릿이 없는 환경(예: CI 체크아웃)에서는 컴파일러가 이 항목을 조용히 건너뛴다.
Source: "secrets\client_secret.json"; DestDir: "{userappdata}\SilentStream"; Flags: onlyifdoesntexist skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName} 제어판"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} 제거"; Filename: "{uninstallexe}"

[Run]
; 폰 원격 제어(LAN)를 선택하면 Windows 방화벽 인바운드 규칙을 추가(확장계획서 §4.4/E5).
; 관리자 권한이 없으면 실패할 수 있으나 앱 실행 시 런타임에서도 best-effort 로 재시도한다.
Filename: "netsh"; \
    Parameters: "advfirewall firewall add rule name=""SilentStream Remote 8787"" dir=in action=allow protocol=TCP localport=8787"; \
    Flags: runhidden; Check: RemoteEnabled
Filename: "{app}\{#MyAppExeName}"; Description: "지금 SilentStream 시작"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 작업 스케줄러 자동 시작 해제 (등록돼 있을 때만)
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""SilentStream AutoStart"" /F"; \
    Flags: runhidden skipifdoesntexist; RunOnceId: "RemoveSchedTask"
; 원격 제어 방화벽 규칙 제거 (있을 때만)
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""SilentStream Remote 8787"""; \
    Flags: runhidden skipifdoesntexist; RunOnceId: "RemoveRemoteFwRule"

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
  RemotePage: TInputOptionWizardPage;

{ 폰 원격 제어(LAN) 사용 여부 — [Run] Check 및 config 작성에 사용 }
function RemoteEnabled: Boolean;
begin
  Result := RemotePage.Values[0];
end;

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

  { 폰 원격 제어 사용 여부 (확장계획서 §4.4 / E5) }
  RemotePage := CreateInputOptionPage(RecordingDirPage.ID,
    '폰 원격 제어', '스마트폰으로 시간표·라이브를 원격 제어할까요?',
    '같은 와이파이의 폰 브라우저로 접속해 교시 시간표 입력과 라이브 시작/중지가 가능합니다.' + #13#10 +
    '(포트 8787 인바운드 허용 / 설치 후 제어판·설정에서 변경 가능)', False, False);
  RemotePage.Add('폰 원격 제어 사용 (LAN, 같은 와이파이)');
  RemotePage.Values[0] := False;
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
  ConfigDir, ConfigFile, Autostart, RemoteMode, Json: string;
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
      if RemoteEnabled then
        RemoteMode := 'lan'
      else
        RemoteMode := 'off';
      Json :=
        '{' + #13#10 +
        '  "version": 2,' + #13#10 +
        '  "recording": { "enabled": true, "folder": "' +
             JsonEscape(RecordingDirPage.Values[0]) + '",' + #13#10 +
        '                 "maxSizeGb": 100, "retentionDays": 7, "minFreeGb": 5 },' + #13#10 +
        '  "remote": { "mode": "' + RemoteMode + '", "port": 8787, "deviceTokens": [] },' + #13#10 +
        '  "autostart": "' + Autostart + '"' + #13#10 +
        '}' + #13#10;
      SaveStringToFile(ConfigFile, Json, False);
    end;
  end;
end;
