# WorkBuddy 每日签到

Windows 托盘小工具：每天自动打开 WorkBuddy 并签到，减少手动领取积分的操作。

## 功能

- 自定义每日签到时间，也可立即签到。
- 启动或唤醒后补签，失败后有限重试。
- 识别今日已签到，避免重复操作。
- 关闭窗口后留在托盘，支持暂停和退出。
- 本机加密保存登录状态，开机启动可选。

## 使用

1. 从 [Releases](https://github.com/shiyu01753-dot/WorkBuddyDaily/releases) 下载 ZIP，完整解压后运行 `WorkBuddyDaily.exe`。
2. 点击“关联账号”，在腾讯网页完成授权。
3. 核对 WorkBuddy 程序路径，按需调整时间并保存。

关联账号并识别程序后，默认每天 **09:00（北京时间）** 执行；当天已过该时间会补签。开机启动默认关闭。

支持 Windows 10/11，需要 .NET Framework 4.8。目前仅支持国内版签到，程序运行时才能执行计划。发行包不含账号或个人配置。

<details>
<summary>界面预览</summary>

![界面预览](docs/ui.png)

</details>

<details>
<summary>开发与验证</summary>

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
New-Item -ItemType Directory .\qa -Force | Out-Null
Start-Process .\WorkBuddyDaily.exe -ArgumentList '--self-test', "$PWD\qa\check.txt" -Wait
```

自检使用模拟服务，不执行真实签到。接口格式参考 [workbuddy-helper](https://github.com/autumnsentiment/workbuddy-helper)，积分规则见 [腾讯文档](https://www.codebuddy.cn/docs/workbuddy/Credits)。本工具非腾讯官方产品。

</details>
