; Windows installer for Kadr (Inno Setup 6). Built on GitHub: ISCC installer\windows\kadr.iss
; Installs for the current user only (no administrator password), into %LOCALAPPDATA%\Programs\Kadr.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6F3B2D1A-8C4E-4B7A-9E21-5D0C7A4B9F13}
AppName=Kadr
AppVerName=Kadr {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=Kadr
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Kadr
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\..\dist
OutputBaseFilename=Kadr-Setup-Windows
SetupIconFile=..\..\icons\kadr.ico
UninstallDisplayIcon={app}\Kadr.exe
UninstallDisplayName=Kadr
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[CustomMessages]
ru.Autostart=Запускать Кадр вместе с Windows
uk.Autostart=Запускати Кадр разом із Windows
en.Autostart=Start Kadr with Windows
de.Autostart=Kadr mit Windows starten
fr.Autostart=Lancer Kadr au démarrage de Windows
es.Autostart=Iniciar Kadr con Windows
it.Autostart=Avvia Kadr con Windows
ru.SendTo=Пункт «Кадр (загрузить)» в меню «Отправить» для картинок
uk.SendTo=Пункт «Кадр (завантажити)» у меню «Надіслати» для картинок
en.SendTo=“Kadr (upload)” in the Send to menu for images
de.SendTo=„Kadr (hochladen)“ im Menü „Senden an“ für Bilder
fr.SendTo=« Kadr (envoyer) » dans le menu Envoyer vers pour les images
es.SendTo=«Kadr (subir)» en el menú Enviar a para imágenes
it.SendTo=«Kadr (carica)» nel menu Invia a per le immagini
ru.SendToName=Кадр (загрузить)
uk.SendToName=Кадр (завантажити)
en.SendToName=Kadr (upload)
de.SendToName=Kadr (hochladen)
fr.SendToName=Kadr (envoyer)
es.SendToName=Kadr (subir)
it.SendToName=Kadr (carica)

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"
Name: "sendto"; Description: "{cm:SendTo}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\..\Kadr.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Kadr"; Filename: "{app}\Kadr.exe"
Name: "{autodesktop}\Kadr"; Filename: "{app}\Kadr.exe"; Tasks: desktopicon
Name: "{usersendto}\{cm:SendToName}"; Filename: "{app}\Kadr.exe"; Tasks: sendto

[Registry]
; the same value Kadr's own "Start with Windows" setting uses
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Kadr"; ValueData: """{app}\Kadr.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Kadr.exe"; Description: "{cm:LaunchProgram,Kadr}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Kadr.exe /F"; Flags: runhidden; RunOnceId: "StopKadr"
