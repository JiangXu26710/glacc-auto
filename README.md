<div align="center">

# glacc-auto

自动领取给梨加速器时长。

> ### ⚠️ 请先阅读
> 本项目**仅供个人学习、研究与技术交流**，**严禁任何商业或盈利性使用**。
> 本软件为非官方第三方工具，与任何第三方服务提供方**无任何关联**。
> 使用可能带来的账号限制、权益损失及法律风险**由使用者自行承担**。
> 完整条款见 **[LICENSE](LICENSE)** 与 **[DISCLAIMER.md](DISCLAIMER.md)**。

---

## 软件截图

<img src=".\static\screenshot\img_1.png" alt="img_1" style="zoom:50%;" />

## 功能特性

- **一键领取** —— 登录后点一次按钮，自动跑完当日全部任务，无需人工干预
- **余额一览** —— 首页直接显示当前可用时长（时 / 分）
- **定时领取** —— 由 Windows 计划任务在每天指定时间自动执行
- **结果通知** —— 可选接入 Server酱，定时领取结束后把结果推送到微信
- **原生观感** —— Win11 Fluent 风格界面，支持跟随系统深浅色与界面缩放

## 下载

前往 **[Releases](https://github.com/JiangXu26710/glacc-auto/releases/latest)** 下载最新的 `glacc-auto-v*.zip`，解压到任意目录，双击 `GlaccAuto.Gui.exe` 即可运行。

- **系统要求**：Windows 10 / 11，64 位

## 常见问题

**定时领取未生效？**

- 确保设置页的开关处于开启状态，并检查 Windows 任务计划程序中是否存在 `glacc-auto-claim` 任务
- 确保到达预定时间后，电脑处于开机已登录状态
- 确保杀毒软件不会误报、拦截本软件

## 数据与隐私

- 手机号、账号 ID、登录令牌**仅保存在本机** `%APPDATA%\glacc-auto\` 目录下，不会上传到任何第三方服务器
- 程序不收集、不上报任何使用数据与统计信息

## 从源码构建

需要 .NET 10 SDK、Windows 11 SDK 与 MSVC 生成工具（Visual Studio Build Tools 的 C++ 生成工具）。仓库根目录的 `package.ps1` 提供了一键打包：

```powershell
.\package.ps1                # 发布 + 暂存 + 打包 zip（版本号读自 GlaccAuto.Gui.csproj）
.\package.ps1 -Launch        # 打包后直接启动
.\package.ps1 -Version 0.2.0 # 覆盖版本号
```

若工具链装在非默认位置，可用 `-BuildToolsRoot` 指定，或设置环境变量 `GLACC_BUILDTOOLS`。

## 许可

本项目以 **[PolyForm Noncommercial License 1.0.0](LICENSE)** 授权：

- ✅ 允许个人学习、研究、实验、业余爱好等**非商业用途**
- ✅ 允许修改、二次开发与再分发
- ❌ **禁止任何商业或盈利性使用**
- 📌 分发时必须保留版权声明（见 [NOTICE](NOTICE)）与许可条款文本

> 注意：这是**非商业许可**，不等于 OSI 认可的开源许可。源码公开、可自由用于非商业目的，但**不可商用**。

使用前请务必阅读 **[DISCLAIMER.md](DISCLAIMER.md)**。

---

Copyright (c) 2026 JiangXu26710
