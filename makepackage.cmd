set RepoRoot=%~dp0
set RepoRoot=%RepoRoot:~0,-1%

call cibuild.cmd --scope build --config Release --bootstrap-only

rd /s /q bin\Bootstrap\MSBuild\15.0\Bin\SdkResolvers\NuGet.MSBuildSdkResolver

nuget.exe pack setup\MsBuild.Engine.Corext\MsBuild.Engine.Corext.nuspec -NonInteractive -OutputDirectory bin\Setup -Properties version=%1;repoRoot=%RepoRoot%;X86BinPath=%~dp0bin\Release\x86\Windows_NT\Output;X64BinPath=%~dp0bin\Release\x64\Windows_NT\Output -Verbosity Detailed

md \\fsu\shares\MsEng\MSBuild\raines\dd_558508\%1

robocopy /mt:128 C:\src\msbuild\bin\Release\x64\Windows_NT\Output\ \\fsu\shares\MsEng\MSBuild\raines\dd_558508\%1\ Microsoft.Build*dll Microsoft.Build*pdb MSBuild.dll MSBuild.pdb /xf *unittests*