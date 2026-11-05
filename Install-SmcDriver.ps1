# Ensures the signed AppleSMC kernel driver service is registered and running even if Macs Fan Control is uninstalled
$driverPath = Join-Path $PSScriptRoot "applesmc.sys"
$svc = Get-Service -Name "AppleSMC" -ErrorAction SilentlyContinue
if (-not $svc) {
    sc.exe create AppleSMC type= kernel start= auto binPath= "$driverPath" DisplayName= "Apple SMC Service"
}
Start-Service -Name "AppleSMC"
Write-Host "AppleSMC driver service is running."
