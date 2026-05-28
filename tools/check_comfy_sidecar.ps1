$ErrorActionPreference = "Stop"
$python = "C:\Users\rxcam\ComfyUI_portable\ComfyUI_windows_portable\python_embeded\python.exe"
$out = "artifacts\first-field\logs\comfy_sidecar_probe.txt"
$lines = @()
$lines += (& $python --version)
$lines += (& $python -c "import torch; print('torch=' + torch.__version__); print('cuda=' + str(torch.cuda.is_available())); print('device=' + (torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'none'))")
$nvcc = Get-Command nvcc -ErrorAction SilentlyContinue
$lines += "nvcc=$($nvcc.Source)"
$lines += "reason=DreamScene360 currently documents Python 3.10/3.11, so Python 3.13 should use an isolated sidecar."
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
Set-Content -LiteralPath $out -Value $lines
Write-Host "Wrote $out"
