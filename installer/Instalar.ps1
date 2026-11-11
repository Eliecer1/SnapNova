# Instalador de APPSHOTACREEN (no requiere permisos de administrador)
$ErrorActionPreference = 'Stop'
$app    = 'APPSHOTACREEN'
$dest   = Join-Path $env:LOCALAPPDATA "Programs\$app"
$src    = Join-Path $PSScriptRoot 'AppShotScreen.exe'
$exe    = Join-Path $dest 'AppShotScreen.exe'
$menu   = Join-Path ([Environment]::GetFolderPath('Programs')) "$app.lnk"
$inicio = Join-Path ([Environment]::GetFolderPath('Startup')) "$app.lnk"
$escrit = Join-Path ([Environment]::GetFolderPath('Desktop')) "$app.lnk"

Write-Host ''
Write-Host "  Instalando $app..." -ForegroundColor Cyan

if (-not (Test-Path $src)) { throw "No encuentro AppShotScreen.exe junto a este instalador. Descomprime todo el .zip antes de instalar." }

# Si ya está abierta, la cerramos para poder reemplazarla.
Get-Process AppShotScreen -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $src $exe -Force
Copy-Item (Join-Path $PSScriptRoot 'Desinstalar.ps1') $dest -Force
Unblock-File $exe -ErrorAction SilentlyContinue

function New-Shortcut($path, $arguments = '') {
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
    $sh.TargetPath = $exe
    $sh.Arguments = $arguments
    $sh.WorkingDirectory = $dest
    $sh.IconLocation = "$exe,0"
    $sh.Description = 'Capturas de pantalla con editor'
    $sh.Save()
}

New-Shortcut $menu
New-Shortcut $escrit
Write-Host '  - Acceso directo en el menú Inicio y en el escritorio'

$r = Read-Host '  ¿Iniciar APPSHOTACREEN automáticamente al encender la PC? (S/N)'
if ($r -match '^[sSyY]') { New-Shortcut $inicio '--tray'; Write-Host '  - Se iniciará con Windows' }
elseif (Test-Path $inicio) { Remove-Item $inicio -Force }

# Registro en "Aplicaciones instaladas" de Windows para poder desinstalar desde Configuración.
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$app"
New-Item -Path $key -Force | Out-Null
Set-ItemProperty $key DisplayName $app
Set-ItemProperty $key DisplayVersion '0.1.0'
Set-ItemProperty $key Publisher $app
Set-ItemProperty $key DisplayIcon "$exe,0"
Set-ItemProperty $key InstallLocation $dest
Set-ItemProperty $key UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\Desinstalar.ps1`""
Set-ItemProperty $key NoModify 1 -Type DWord
Set-ItemProperty $key NoRepair 1 -Type DWord

Start-Process $exe
Write-Host ''
Write-Host "  Listo. $app quedó instalado en $dest" -ForegroundColor Green
Write-Host '  Busca el icono azul junto al reloj. Impr Pant / Ctrl+Shift+S = región, Ctrl+Shift+D = con desplazamiento.'
Write-Host ''
Read-Host '  Pulsa Enter para cerrar'
