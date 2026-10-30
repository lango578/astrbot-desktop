# AstrBot 桌面版（Windows，非官方构建）

把 [AstrBot](https://github.com/AstrBotDevs/AstrBot) 打成一台普通电脑上双击就能用的 Windows 程序：
原生启动器窗口 + 无控制台后台核心 + 内嵌管理面板 + QQ 接入。**这不是 AstrBot 官方项目**，
上游版权与许可证归原作者，本仓库只包含我自己写的打包/启动器/验收代码与构建产物。

## 快速开始

1. 到 [Releases](releases/latest) 下载 `AstrBot-Setup-*.exe`（安装版）或 `AstrBot-*-portable.zip`（免安装，解压即用）
2. 双击，等窗口出现。左上角状态灯变绿就是起来了
3. 窗口里会显示**面板初始密码**（用户名 `astrbot`）→ 登录后先去面板改掉它
4. 要接 QQ：右侧选「QQ 官方机器人」→ 点**扫码登录** → 用手机 QQ 扫码，不用手抄 AppID/AppSecret

## 这个版本做了什么

| | |
|---|---|
| 界面 | 深色圆角窗口（Win11 风格标题栏/Mica）、系统托盘、关闭缩到托盘、开机自启可选 |
| 无命令行 | 启动器和核心都是 GUI 子系统程序，结构上弹不出黑框；子进程（ffmpeg 等）也注入 `CREATE_NO_WINDOW` |
| QQ 接入 | OneBot v11（NapCat/Lagrange/go-cqhttp）、QQ 官方机器人（Websocket/Webhook）、Satori；表单校验 + **面板扫码登录内置**；**个人号那条路的客户端也由启动器发现、起停、看登录状态、把它出的二维码显示在本程序窗口里**，并可选按固定版本+SHA-256 校验下载 NapCat（对不上摘要就拒装） |
| 面板 | WebView2 内嵌在窗口里；没有 WebView2 运行时的机器自动退回系统浏览器，不白屏 |
| 插件 | 依赖自动装进 `data\site-packages`；PyPI 默认走国内镜像、GitHub 归档默认走加速前缀（失败自动回退直连） |
| 出错自己修 | 端口被占自动顺延并记住、面板启动超时看门狗、崩溃按 3/10/30 秒退避重启（最多 3 次）、故障分类给人话建议、一键导出**已脱敏**诊断包 |
| 已知 bug 修复 | OneBot/NapCat 重连后被旧连接踢下线（机器人看着在线、消息永远发不出去）——**直接改在程序里**，不装插件、不改上游源码 |

## 下载前要知道的两件事

1. **Windows 仍会提示「未知发布者」。** 两个 exe 和安装包都带本次构建的自签代码签名
   （附 `keys/astrbot-signing.cer`，改一个字节就会被报 `HashMismatch`），但自签证书没有权威机构背书，
   SmartScreen 该弹还是弹。这不是文件被篡改的信号；要彻底不弹需要购买正式代码签名证书。
2. **校验再装。** 下载 `SHA256SUMS.txt`，用 `certutil -hashfile 文件名 SHA256` 比对；
   源码树另有 `SOURCE-SHA256.txt`。

## 从源码重建

```powershell
# 1) 取上游源码（本仓库不含 AstrBot 源码，见 MODIFICATIONS.md 的版本与改动说明）
#    放到 src\AstrBot-master，并准备 venv（Python 3.12）+ 依赖 + PyInstaller
# 2) 打核心
venv\Scripts\python.exe -m PyInstaller --noconfirm --distpath dist-new build\AstrBot.spec
# 3) 编启动器（只用系统自带 csc，无第三方库）
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe `
  /out:build\launcher\AstrBot.exe /win32icon:build\assets\astrbot.ico `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll `
  /reference:build\vendor\webview2\Microsoft.Web.WebView2.Core.dll `
  /reference:build\vendor\webview2\Microsoft.Web.WebView2.WinForms.dll `
  launcher\AstrBotLauncher.cs launcher\AstrBotControls.cs launcher\AstrBotPanelView.cs `
  launcher\AstrBotLauncherUi.cs launcher\AstrBotRecovery.cs launcher\AstrBotBind.cs
# 4) 组装交付目录 → 签名 → 打安装程序
venv\Scripts\python.exe build\package_dist.py
powershell -File build\make_signing_cert.ps1 ; powershell -File build\sign_dist.ps1
tools\innosetup\ISCC.exe /DSourceDir="...\dist-new\AstrBot" build\setup_astrbot.iss
```

## 验收（这个版本是真跑过测试的，不是"应该能用"）

`test/` 下每个脚本都是一条能重跑的验收，全部只走 `127.0.0.1`：

| 脚本 | 盯什么 |
|---|---|
| `verify_app.py` | 核心能起来、PE 子系统确实是 GUI（结构上不可能弹黑框） |
| `verify_desktop_app.py` | 从空 data 双击起步：启动器拉核心 → 面板 200 → mock OneBot 推进 `/help` 收到回包 → 退出无残留 |
| `verify_recovery.py` | 真注入三种故障：端口被占、核心被外部杀掉、配置文件写坏 |
| `verify_onebot_recovery.py` | 复现「重连后变聋」并验证修复（关掉补丁必须红，装插件必须绿） |
| `verify_plugin_sources.py` | 国内源默认值 + 加速源与直连**逐字节一致** + 真按仓库地址装一个插件 |
| `verify_qq_bind.py` | 扫码登录：建任务、出二维码、轮询、密钥不外泄、核心在跑时拒写配置 |
| `verify_signature.py` / `build/signature_proof.ps1` | 签名归属 + 改一个字节必须报 `HashMismatch` |
| `verify_installer.py` / `verify_plugin_upload.py` | 安装程序三语（简/繁/英）、不装不删 `data\`、面板上传插件这条路真能走通 |
| `--onebottest` + `mock_napcat_client.py` | 个人号那条新链路：按真标志文件认出 NapCat、读它自己的 `webui.json`、调它自带脚本起停、端口判活、取回登录二维码并画成 PNG、登录阶段问到 `online`、按 PID 杀干净不留残留；带本地包时再验字节闸门（改坏一字节必须拒装、解压挡路径穿越） |
| `--uitest` 里的响应性断言 | 灌 1200 行日志时量消息循环延迟：逐行投递（旧写法）实测 **2516 ms**，批量写 **0 ms**；再把客户端面板换成"连得上不回话"的黑洞端口，连撞三次轮询界面延迟仍为 **0 ms** |
| `UiGuard` + 构建戳 | 凡可能在 UI 线程上做网络/起进程/扫目录的地方都留一句 `UiGuard.Note(...)`，诊断包原样列出；`--uitest` 最后一项断言"这一路没人偷在 UI 线程上做 I/O"。它当场抓出一次性通道、面板端口探活、启动时扫客户端目录三处（最后那处只在装了 NapCat 的机器上犯，构建机上测不出来）。构建戳由 `build\make_stamp.py` 把源码指纹写进标题栏与诊断包，"您跑的是哪一份"从此可比 |
| `audit_personal_info.py` | 交付物与已发布源码里的**身份指纹**审计：账户名/计算机名/账号/域名/服务器 IP/邮箱手机号/令牌/私钥/凭据真值/使用者数据文件；厂商二进制单独定罪，扫描器自己的探针还要 `--self-test` 证明抓得到、不误报 |
| `verify_published_repo.py` | 发布后不带 token 的匿名验收：仓库可见、zipball 与本地 staging 逐字节一致、每个 blob 哈希相等、资产远端 sha256 等于本地、前 4 KB 头对 |
| `--onebottest` 里的离线副本 | 断网也能装客户端：包里 `factory\napcat\` 带 NapCat v4.18.41 两个压缩包（上游许可允许随全文再分发），装的时候仍按同一串 SHA-256 逐字节核对，改一个字节就拒装 |
| `--repairtest` | 「一键修复」的红绿对照：真注入坏配置、缺目录、缺文件、被占端口、孤儿核心、被改坏的 png，跑完断言每一项要么被修好并复验、要么如实报"需要你处理"；同时断言修复器没在 UI 线程上做 I/O |

一条命令跑全套：`venv\Scripts\python.exe test\run_all.py`（14 步、286 项断言（外网那 1 项不可达时会显式记 NOTRUN），全部对着打包后的
`dist-new\AstrBot` 跑，不是对着源码目录跑）。安装程序本身另算 19 项：`test\verify_installer.py`
（装一遍再卸一遍，含三语文案与「不删 data\」）。
最后还有一步"测你真拿到的那份"：把 `AstrBot-Setup-4.29.3.exe` 静默装进临时目录，用
`ASTRBOT_APP_DIR` 指着**装完的目录**再跑一遍 `verify_desktop_app`（10 项，含 mock OneBot 推进 `/help`
真收到回包），并核对安装后的两个 exe 与构建产物逐字节一致、签名与时间戳仍在。验完卸掉、临时目录删除。

## 许可

- AstrBot 本体：**AGPL-3.0**，原文见 `LICENSE-AstrBot.txt`，版权归 AstrBotDevs。本仓库的构建脚本与产物按其条款提供对应源码（本仓库 + 上游）。
- `build/` `launcher/` `test/` 里我自己写的部分：同用 AGPL-3.0，与 AstrBot 保持一致，避免授权比上游更严。
- 不包含、也不授权再分发：第三方二进制（ffmpeg、WebView2 Loader、便携 CPython）、AstrBot 上游源码本身。
- 图标使用 AstrBot 原图，仅做了圆角与居中处理，版权归原项目。
