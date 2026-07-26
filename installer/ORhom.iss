#ifndef AppVersion
  #error AppVersion must be supplied with /DAppVersion=x.y.z
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

#ifndef VcRedistPath
  #error VcRedistPath must point to the official Microsoft x64 redistributable
#endif

#ifndef NoticesPath
  #error NoticesPath must point to THIRD-PARTY-NOTICES.md
#endif

#dim VcRedistVersion[4]
#define VcRedistVersionText GetVersionComponents(VcRedistPath, VcRedistVersion[0], VcRedistVersion[1], VcRedistVersion[2], VcRedistVersion[3])

[Setup]
AppId={{A8CDE7E8-9B1B-4EAC-B644-EA4E58C01E34}
AppName=ORhom
AppVersion={#AppVersion}
AppVerName=ORhom {#AppVersion}
AppPublisher=ORhom
AppPublisherURL=https://github.com/Oliveronworking/ORhom
AppSupportURL=https://github.com/Oliveronworking/ORhom/issues
AppUpdatesURL=https://github.com/Oliveronworking/ORhom/releases/latest
DefaultDirName={localappdata}\Programs\ORhom
DefaultGroupName=ORhom
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\ORhom.exe
OutputDir={#OutputDir}
OutputBaseFilename=ORhom-Setup-{#AppVersion}-win-x64
SetupIconFile=..\Assets\ORhom.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupArchitecture=x64
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible and not arm64
MinVersion=10.0.22000
AppMutex=ChatGptDictationBridge.SingleInstance
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=ORhom
VersionInfoDescription=ORhom Installer
VersionInfoProductName=ORhom
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Verknüpfungen:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\ORhom.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#NoticesPath}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#VcRedistPath}"; DestDir: "{tmp}"; DestName: "vc_redist.x64.exe"; Flags: deleteafterinstall; Check: VcRuntimeRequired

[Icons]
Name: "{autoprograms}\ORhom"; Filename: "{app}\ORhom.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ORhom"; Filename: "{app}\ORhom.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Microsoft Visual C++-Laufzeit wird installiert ..."; Verb: "runas"; Flags: shellexec waituntilterminated; Check: VcRuntimeRequired; AfterInstall: VerifyVcRuntime
Filename: "{app}\ORhom.exe"; Description: "ORhom starten"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function VcRuntimeRequired: Boolean;
var
  Installed: Cardinal;
  InstalledVersion: Int64;
  RequiredVersion: Int64;
begin
  RequiredVersion := PackVersionComponents(
    {#VcRedistVersion[0]},
    {#VcRedistVersion[1]},
    {#VcRedistVersion[2]},
    {#VcRedistVersion[3]});
  Result :=
    not RegQueryDWordValue(
      HKLM64,
      'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
      'Installed',
      Installed) or
    (Installed <> 1) or
    not GetPackedVersion(
      ExpandConstant('{sys}\vcruntime140.dll'),
      InstalledVersion) or
    (ComparePackedVersion(InstalledVersion, RequiredVersion) < 0);
end;

procedure VerifyVcRuntime;
begin
  if VcRuntimeRequired then
    RaiseException(
      'Die Microsoft Visual C++-Laufzeit konnte nicht vollständig installiert werden.');
end;
