#ifndef PayloadDir
  #error PayloadDir must be supplied by build-setup.ps1
#endif
#ifndef SetupOutputDir
  #define SetupOutputDir "..\artifacts\setup"
#endif

[Setup]
AppId={{2A4A4E96-436E-4532-9690-4E9B7F20388C}
AppName=Muhabbet Kuşu
AppVersion=1.1.0
AppPublisher=Muhabbet Kuşu
DefaultDirName={localappdata}\MuhabbetKusu
DefaultGroupName=Muhabbet Kuşu
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableProgramGroupPage=yes
DisableDirPage=yes
WizardStyle=modern
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\MuhabbetKusu.exe
LicenseFile=..\LICENSE
OutputDir={#SetupOutputDir}
OutputBaseFilename=MuhabbetKusu-Setup-1.1.0-x64
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"
Name: "downloadmodels"; Description: "Ses modellerini kurulum sırasında indir (internet gerekir)"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "models\*,outputs\*,*.pdb,*.lib,__pycache__\*,runtime\python\Lib\site-packages\torch\include\*"

[Icons]
Name: "{group}\Muhabbet Kuşu"; Filename: "{app}\MuhabbetKusu.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Muhabbet Kuşu"; Filename: "{app}\MuhabbetKusu.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MuhabbetKusu.exe"; Description: "Muhabbet Kuşu'nu aç"; Flags: nowait postinstall skipifsilent

[Code]
var
  ModelsDeferred: Boolean;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  Launched: Boolean;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('downloadmodels') then
  begin
    WizardForm.StatusLabel.Caption := 'Ses modelleri indiriliyor. İlk indirme birkaç dakika sürebilir...';
    WizardForm.FilenameLabel.Caption := 'İndirme tamamlanamazsa uygulama ilk açılışta yeniden deneyecek.';
    Launched := Exec(ExpandConstant('{app}\runtime\python\python.exe'),
      '-u "' + ExpandConstant('{app}\bridge\prepare_models.py') + '" --models-dir "' +
      ExpandConstant('{app}\models') + '"', ExpandConstant('{app}'), SW_HIDE,
      ewWaitUntilTerminated, ExitCode);
    ModelsDeferred := (not Launched) or (ExitCode <> 0);
    if ModelsDeferred then
      Log('Ses modelleri uygulama ilk açılışında indirilecek.');
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and ModelsDeferred then
    WizardForm.FinishedLabel.Caption := 'Kurulum tamamlandı. Ses modelleri ilk açılışta indirilecek. İnternet bağlantınızı açık tutun.';
end;
