<#
.SYNOPSIS
  Publica o SmartGlue CLP Gateway como executável self-contained para o PC da cabine.

.EXAMPLE
  .\scripts\publish.ps1
  .\scripts\publish.ps1 -OutputPath 'D:\deploy\clp-gateway'
#>
[CmdletBinding()]
param(
  [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\publish'),
  [ValidateSet('Release', 'Debug')]
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\SmartGlue.ClpGateway\SmartGlue.ClpGateway.csproj'
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

Write-Host "Publicando $project" -ForegroundColor Cyan
Write-Host "  destino : $OutputPath"
Write-Host "  config  : $Configuration"

# Self-contained de propósito: o PC da cabine não deve depender de um runtime .NET instalado
# que alguém possa atualizar ou remover sem saber que a célula depende dele.
dotnet publish $project `
  --configuration $Configuration `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:DebugType=embedded `
  --output $OutputPath

if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou com código $LASTEXITCODE." }

Write-Host ''
Write-Host 'Publicado.' -ForegroundColor Green
Write-Host "Executável: $(Join-Path $OutputPath 'SmartGlue.ClpGateway.exe')"
Write-Host ''
Write-Host 'Antes de instalar o serviço, crie o appsettings.Local.json no destino com o'
Write-Host 'JwtSettings:Secret do backend e o Backend:BaseUrl da célula. Ver README.'
