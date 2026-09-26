@echo off
rem Runs HealthTech outside Visual Studio with Whisper on the discrete GPU (Intel Arc A370M).
rem GGML_VK_VISIBLE_DEVICES=1 makes ggml see only Vulkan device 1; the app then uses GpuDevice=0
rem (the first and only visible device) automatically, see WhisperOptions.ResolveGpuDevice.
setlocal
set GGML_VK_VISIBLE_DEVICES=1
if "%ASPNETCORE_ENVIRONMENT%"=="" set ASPNETCORE_ENVIRONMENT=Development
cd /d "%~dp0"
dotnet run --project "%~dp0HealthTech.csproj" --launch-profile http %*
endlocal
