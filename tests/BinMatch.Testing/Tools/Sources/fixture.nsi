Unicode true
Name "BinMatch compiled NSIS fixture"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\BinMatchFixture"
RequestExecutionLevel admin

Section
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\BinMatchFixture" "DisplayName" "BinMatch compiled NSIS fixture"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\BinMatchFixture" "DisplayVersion" "1.0.0"
SectionEnd
