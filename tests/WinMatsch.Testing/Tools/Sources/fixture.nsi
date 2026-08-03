Unicode true
Name "WinMatsch compiled NSIS fixture"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\WinMatschFixture"
RequestExecutionLevel admin

Section
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\WinMatschFixture" "DisplayName" "WinMatsch compiled NSIS fixture"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\WinMatschFixture" "DisplayVersion" "1.0.0"
SectionEnd
