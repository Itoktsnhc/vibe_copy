#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Remove-Item -Recurse -Force publish -ErrorAction SilentlyContinue
dotnet publish -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:PublishTrimmed=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o publish
$exe = Get-Item publish/VibeCopy.exe
"{0}  {1:N1} MB" -f $exe.FullName, ($exe.Length / 1MB)
