<#
.SYNOPSIS
  Remove o serviço do SmartGlue CLP Gateway.

.DESCRIPTION
  Parar este serviço derruba o watchdog da máquina: em até 2 segundos todas as estações vão
  para FAULT 1009 HEARTBEAT_LOST e só saem de lá quando um mestre voltar. Faça isso com a
  célula parada.

  O rastro de execuções em C:\ProgramData\SmartGlue\clp-gateway NÃO é apagado.
#>
[CmdletBinding()]
param(
  [string]$ServiceName = 'smartglue-clp-gateway'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
      ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Execute este script em um PowerShell como Administrador.'
}

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
  Write-Host "Serviço $ServiceName não está instalado." -ForegroundColor Yellow
  return
}

Write-Host "Parando $ServiceName" -ForegroundColor Cyan
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
sc.exe delete $ServiceName | Out-Null

Write-Host "Serviço $ServiceName removido. O rastro de execuções foi preservado." -ForegroundColor Green
