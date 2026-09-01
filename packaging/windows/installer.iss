; Inno Setup script for Capture. Compiled by CI (`iscc`) against a self-contained
; `dotnet publish` output. Pass both defines on the command line:
;   iscc installer.iss /DVersion=1.2.3 /DPublishDir=C:\path\to\publish\output
;
; Unsigned for now (see README's Packaging & distribution section) — SignTool= can be added to
; [Setup] later once a code-signing certificate exists, with no other changes needed here.

#ifndef Version
  #define Version "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\src\Capture.App\bin\Release\net8.0\win-x64\publish"
#endif

[Setup]
AppId={{8408B821-8B5A-40E3-BB59-45A54E70F267}
AppName=Capture
AppVersion={#Version}
; Without this, Inno Setup defaults AppVerName to "AppName AppVersion", which shows up as the bold
; entry name in Windows' Installed Apps list (e.g. "Capture version 0.1.0-ci.4") — the version is
; already shown on its own via AppVersion/DisplayVersion, so keep the name itself just "Capture".
AppVerName=Capture
AppPublisher=Fybre
DefaultDirName={autopf}\Capture
DefaultGroupName=Capture
UninstallDisplayIcon={app}\Capture.App.exe
OutputDir=out
; Deliberately not version-suffixed: the README links to
; github.com/Fybre/Capture/releases/latest/download/CaptureSetup.exe, which only resolves correctly
; when every release's asset has this exact same filename — the release itself (tag/title) still
; carries the version, visible on the GitHub release page.
OutputBaseFilename=CaptureSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Capture"; Filename: "{app}\Capture.App.exe"
Name: "{group}\Uninstall Capture"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Capture"; Filename: "{app}\Capture.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Capture.App.exe"; Description: "Launch Capture"; Flags: nowait postinstall skipifsilent
