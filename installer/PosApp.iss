#ifndef MyAppVersion
  #define MyAppVersion "1.4.14"
#endif

#ifndef MyAppNumericVersion
  #define MyAppNumericVersion "1.4.14.0"
#endif

#ifndef MyAppSourceDir
  #define MyAppSourceDir "..\publish"
#endif

#define MyAppName "PosApp"
#define MyAppPublisher "PosApp"
#define MyAppExeName "PosApp.exe"

[Setup]
AppId={{4F99C8E4-2B48-4BE7-B4A5-2F5EAB8D7C21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) 2026 {#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
CreateUninstallRegKey=yes
Uninstallable=yes
LicenseFile=LICENSE.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=PosApp-{#MyAppVersion}-Setup
SetupIconFile=..\src\PosApp.Wpf\Assets\PosApp.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
WizardImageFile=Assets\WizardImage.bmp
WizardSmallImageFile=Assets\WizardSmallImage.bmp
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=no
DisableReadyPage=no
DisableFinishedPage=no
PrivilegesRequired=admin
MinVersion=10.0
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
; Business data is intentionally stored under %LOCALAPPDATA%\PosApp and is
; never installed into or deleted from {app}. Keeping the AppId stable makes
; this installer an in-place program-file upgrade without touching store data.
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} offline installer
VersionInfoProductName={#MyAppName}
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoProductVersion={#MyAppNumericVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#MyAppSourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
