; UNIVERSUM installer (Inno Setup 6). Built by GitHub Actions; the version and the folder with the
; published program are passed on the command line (/DAppVersion=..., /DSourceDir=...).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName "UNIVERSUM"
#define AppExe "Universum.exe"

[Setup]
AppId={{3F7A2C91-6B4E-4D8A-9E15-7C2B0D4F8A63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=tarzanislav
AppPublisherURL=https://github.com/tarzanislav/universum
AppComments=Grafická nadstavba nad MKVToolNix
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\UNIVERSUM
DefaultGroupName=UNIVERSUM
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=UNIVERSUM_Setup_{#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
MinVersion=10.0.17763
CloseApplications=yes
LicenseFile=..\LICENSE

[Languages]
#if FileExists(CompilerPath + "Languages\Slovak.isl")
Name: "sk"; MessagesFile: "compiler:Languages\Slovak.isl"
#else
Name: "en"; MessagesFile: "compiler:Default.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\UNIVERSUM"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\UNIVERSUM"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Explorer context menu: a file goes to the UNIVERSUM rules, a folder to batch processing (removed when uninstalling)
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.mkv\shell\UNIVERSUM"; ValueType: string; ValueName: ""; ValueData: "Otvoriť v UNIVERSUM"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.mkv\shell\UNIVERSUM"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.mkv\shell\UNIVERSUM\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Directory\shell\UNIVERSUM"; ValueType: string; ValueName: ""; ValueData: "Spracovať v UNIVERSUM"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Directory\shell\UNIVERSUM"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"
Root: HKA; Subkey: "Software\Classes\Directory\shell\UNIVERSUM\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,UNIVERSUM}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; after an automatic update (silent install with /SPUSTIT=1) UNIVERSUM starts again
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: StartAfterUpdate

[Code]
function StartAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:SPUSTIT|0}') = '1';
end;

{ MKVToolNix is required. UNIVERSUM itself finds it, or downloads the official installer,
  verifies its SHA-256 checksum and signature and runs it (Universum.exe --priprav-mkvtoolnix). }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and not WizardSilent then
  begin
    if not Exec(ExpandConstant('{app}\{#AppExe}'), '--priprav-mkvtoolnix', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode)
       or (ResultCode <> 0) then
      MsgBox('UNIVERSUM zatiaľ nenašiel funkčný MKVToolNix. Bez neho nemôže upravovať súbory MKV.' + #13#10 + #13#10 +
             'Pri každom spustení UNIVERSUM ponúkne jeho stiahnutie a inštaláciu z oficiálnej stránky mkvtoolnix.download.',
             mbInformation, MB_OK);
  end;
end;
