[Setup]
AppId={{7123AA93-1D9A-426E-9E98-3F81C99E0C5A}
AppName=BinMatch compiled Inno fixture
AppVersion=1.0.0
DefaultDirName={autopf}\BinMatchFixture
OutputBaseFilename=fixture-inno
PrivilegesRequired=admin
UninstallDisplayName=BinMatch compiled Inno fixture

[Files]
Source: "{win}\System32\where.exe"; DestDir: "{app}"; Flags: external
