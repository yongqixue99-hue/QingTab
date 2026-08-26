@echo off
chcp 65001 >nul
title 卸载轻页 QingTab

if not exist "%~dp0QingTab.exe" (
    echo 未在当前目录找到 QingTab.exe。
    echo 请把本脚本放回轻页程序目录后再运行。
    pause
    exit /b 1
)

"%~dp0QingTab.exe" --exit
if errorlevel 1 (
    echo.
    echo 轻页驻留进程未能在限定时间内完全退出。
    echo 请保存工作后重试，暂时不要删除程序目录。
    pause
    exit /b 1
)
"%~dp0QingTab.exe" --uninstall
if errorlevel 1 (
    echo.
    echo 卸载清理未完成。文件夹打开设置可能已被其他程序修改，轻页为避免覆盖它而停止。
    echo 请查看 %%LOCALAPPDATA%%\QingTab\QingTab-error.log，暂时不要删除程序目录。
    pause
    exit /b 1
)

echo.
echo 轻页已退出，文件夹新标签接管、开机启动和当前用户状态已经清理。
echo 现在可以关闭此窗口，并手动删除整个程序目录。
pause
