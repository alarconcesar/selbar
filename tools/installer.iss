; Instalador de Selbar (Inno Setup 6 — https://jrsoftware.org/isinfo.php)
;
; Uso:
;   1) .\build.ps1 -SelfContained           (genera dist\Selbar-<version>\)
;   2) iscc tools\installer.iss /DMyVersion=0.13.6
;
; Sin /DMyVersion usa la versión de abajo (mantenerla igual que el .csproj;
; el CI falla si difieren).
; El instalador NO firma el ejecutable:
; para quitar el aviso de SmartScreen hace falta un certificado de code signing
; (signtool con /f cert.pfx /p ...), que no se incluye aquí.

#ifndef MyVersion
  #define MyVersion "0.13.6"
#endif

#define MyName "Selbar"
#define MyExe "Selbar.exe"
#define MyPublisher "Selbar"

; El nombre del mutex de instancia única de la app (App.xaml.cs). Sirve para
; avisar de que Selbar está abierto antes de intentar reemplazar el .exe.
#define MyMutex "Selbar_SingleInstance"

[Setup]
AppId={{6F1C2B54-9A2E-4C7B-9E33-1B7A5D0C4E21}
AppName={#MyName}
AppVersion={#MyVersion}
AppPublisher={#MyPublisher}
DefaultDirName={autopf}\{#MyName}
DefaultGroupName={#MyName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename={#MyName}-{#MyVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyExe}
SetupIconFile=..\src\Selbar\Assets\app.ico
VersionInfoVersion={#MyVersion}
MinVersion=10.0.17763
PrivilegesRequired=lowest
AppMutex={#MyMutex}
; Instalación por usuario (sin admin): casa con el autostart en HKCU y con que
; la app funcione sin escritura en su carpeta. {autopf} apunta entonces a
; %LocalAppData%\Programs.
; El ZIP portable guarda datos junto al .exe; la instalación NO es portable:
; [Files] excluye portable.txt para que la app instalada use %AppData%\Selbar
; (si se copiara, intentaría escribir en su carpeta de instalación).
; No hay sección [UninstallDelete]: al desinstalar se conservan los datos de
; usuario (%AppData%\Selbar). El payload es el publish win-x64
; self-contained; en ARM64 se recomienda el ZIP win-arm64 portable.

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Files]
; Excludes portable.txt: el publish lo trae para el ZIP, pero instalado haría
; que los ajustes se guardaran dentro de {app} en vez de %AppData%\Selbar
; (y se perderían al desinstalar o al reinstalar en otra ruta).
Source: "..\dist\Selbar-{#MyVersion}\*"; DestDir: "{app}"; Excludes: "portable.txt,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyName}"; Filename: "{app}\{#MyExe}"
Name: "{group}\Desinstalar {#MyName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyName}"; Filename: "{app}\{#MyExe}"; Tasks: desktopicon

[Registry]
; Sin esto, tras desinstalar Windows seguía intentando arrancar un .exe que ya
; no existe (el valor Run lo escribe la app, no el instalador).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Selbar"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyExe}"; Description: "Ejecutar {#MyName}"; Flags: nowait postinstall skipifsilent
