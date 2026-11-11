# Desinstalador de SnapNova
$app  = 'SnapNova'
$dest = Join-Path $env:LOCALAPPDATA "Programs\$app"

Get-Process SnapNova -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

foreach ($lnk in @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) "$app.lnk"),
    (Join-Path ([Environment]::GetFolderPath('Startup')) "$app.lnk"),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) "$app.lnk"))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
}
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$app" -Recurse -Force -ErrorAction SilentlyContinue

# El ejecutable se borra al final (este script vive en la misma carpeta).
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden

Write-Host "$app se desinstaló. Tus capturas en Imágenes\SnapNova y tus ajustes no se tocaron." -ForegroundColor Green
Start-Sleep 3
