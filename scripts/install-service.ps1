<#
.SYNOPSIS
  Instala o SmartGlue CLP Gateway como serviço do Windows no PC da cabine.

.DESCRIPTION
  Cria o serviço smartglue-clp-gateway com início automático e política de reinício.

  Reinício automático é parte do desenho, não conveniência: o gateway segura o watchdog da
  máquina. Se o processo morrer e ninguém o levantar, todas as estações caem em
  1009 HEARTBEAT_LOST e a célula para.

  Ao voltar, o gateway NÃO retoma rotina alguma: ele relê o estado da máquina. Uma execução
  interrompida por restart aparece como perdida, nunca como concluída.

.EXAMPLE
  .\scripts\install-service.ps1 -BinaryPath 'C:\SmartGlue\clp-gateway\SmartGlue.ClpGateway.exe'
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$BinaryPath,

  [string]$ServiceName = 'smartglue-clp-gateway',
  [string]$DisplayName = 'SmartGlue CLP Gateway',
  [string]$DataPath = 'C:\ProgramData\SmartGlue\clp-gateway'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
      ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Execute este script em um PowerShell como Administrador.'
}

if (-not (Test-Path $BinaryPath)) { throw "Executável não encontrado: $BinaryPath" }
$BinaryPath = [System.IO.Path]::GetFullPath($BinaryPath)

# O rastro de execuções não fica junto do binário: um deploy substitui a pasta do binário, e
# levar a evidência das execuções junto seria perder o histórico a cada atualização.
New-Item -ItemType Directory -Force -Path (Join-Path $DataPath 'runs') | Out-Null

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
  Write-Host "Serviço $ServiceName já existe; parando e removendo para recriar." -ForegroundColor Yellow
  Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
  sc.exe delete $ServiceName | Out-Null
  Start-Sleep -Seconds 2
}

Write-Host "Criando serviço $ServiceName" -ForegroundColor Cyan
sc.exe create $ServiceName binPath= "`"$BinaryPath`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create falhou com código $LASTEXITCODE." }

sc.exe description $ServiceName "Mestre do CLP da celula Smart Glue. Segura o heartbeat Modbus, executa rotinas de inspecao e atende o desktop em 127.0.0.1:5180." | Out-Null

# Reinicia em 5s nas duas primeiras falhas e em 30s nas seguintes; a contagem zera após 1 dia.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

[Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')
[Environment]::SetEnvironmentVariable('Results__Directory', (Join-Path $DataPath 'runs'), 'Machine')

Write-Host ''
Write-Host "Serviço $ServiceName instalado." -ForegroundColor Green
Write-Host ''
Write-Host 'Ainda falta, antes de iniciar:' -ForegroundColor Yellow
Write-Host "  1. criar $(Split-Path $BinaryPath)\appsettings.Local.json com JwtSettings:Secret"
Write-Host '     (a mesma chave que o smt_glue usa para assinar) e Backend:BaseUrl;'
Write-Host '  2. conferir AxisHardware:Host e ModbusPort apontando para o CLP ou o simulador.'
Write-Host ''
Write-Host "Depois: Start-Service $ServiceName"
Write-Host 'Conferir: Invoke-RestMethod http://127.0.0.1:5180/api/health'
