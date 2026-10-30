# 对上游的改动清单（AGPL 合规说明）

构建对象：**AstrBot 4.29.3**（https://github.com/AstrBotDevs/AstrBot ，AGPL-3.0）。
本仓库**不内联**上游源码；上游源码按版本号自行获取，保证可复现：

```powershell
# 上游 4.29.3 的获取方式（任选其一）
git clone --branch v4.29.3 https://github.com/AstrBotDevs/AstrBot.git src/AstrBot-master
# 或（本机 git 到 github.com:443 不通时）走归档直链
curl -L -o astrbot.zip https://codeload.github.com/AstrBotDevs/AstrBot/zip/refs/tags/v4.29.3
```

WebUI 静态包取自 AstrBot 官方发布的 dashboard 产物，放在
`src/AstrBot-master/astrbot/dashboard/dist`（`assets/version` 必须与核心版本一致，否则启动会联网自取）。

## 一、没有修改上游任何源文件

`src/AstrBot-master` 保持上游原样。所有适配都发生在**我们自己写的文件**里，
升级 AstrBot 时不需要重打补丁：

| 我们的文件 | 作用 |
|---|---|
| `build/astrbot_desktop.py` | 桌面版入口（PyInstaller 用它替代 `main.py` 作为脚本），负责数据目录钉位、环境变量、UTF-8 流、单实例锁、首次配置播种、`--self-check` / `--platform-file` / `--platform-dump` / `--login-bind` / `--qr` 等一次性通道 |
| `build/AstrBot.spec` | 冻结打包配置（数据文件、hiddenimports、dist-info 元数据、插件 .py 落盘） |
| `launcher/*.cs` | 原生启动器（界面、托盘、内嵌面板、纠错、扫码登录窗口） |
| `launcher/AstrBotOneBot.cs` | 发现并接管本机 OneBot 客户端（NapCat Shell / Framework）：调客户端自带的启动脚本、按 PID 精确停止、以 WebUI 端口判活、读它的 `config/webui.json` 拿端口与令牌、问 `/GetQQLoginQrcode` 与 `/CheckLoginStatus` 拿登录二维码与阶段。**v4.18.41 实测两处必须照真机来**：① 路由全在 `/api` 下（无前缀一律 404）；② 配置文件里已无明文令牌，只有 `passwordHash`/`passwordSalt`，要拿面板口令 `POST /api/auth/login`（body `{"hash": sha256(口令+".napcat")}`）换一张 1 小时有效的 `Credential`，之后带 `Authorization: Bearer <Credential>`（或 `?webui_token=`）；面板错误是 HTTP 200 包 `{"code":-1,"message":"Unauthorized"}`，只看状态码会全绿假通过 |
| `launcher/AstrBotOneBotInstall.cs` | 可选的客户端下载器：版本与 SHA-256 固定在代码里，逐字节校验不过就拒装；**优先用随包的离线副本**（`factory\napcat\`），没有副本才走国内加速前缀、再退回直连；解压挡路径穿越；随包落一份「来源与许可说明」。**注意 TLS**：这个 exe 由系统自带 csc 编译、没有 `TargetFrameworkAttribute`，.NET 会按 4.0 默认只给 SSL3/TLS1.0，现代端点直接拒握手（症状是「下不到客户端压缩包：无法连接到远程服务器」）；`Program` 入口显式打开 TLS1.2（Win10 起再带 TLS1.3），并由 `--onebottest` 断言守住 |
| `launcher/AstrBotRepair.cs` | 「一键修复」：九项检查按「检测 → 确认坏了才动手 → 动手后复验」跑，全在后台线程。自动修的只有有把握的那几件（补建目录、坏配置留档、端口顺延并写回设置、按 PID 清掉没人管的孤儿核心、重启我们自己的核心、按 `factory\MANIFEST-SHA256.txt` 核对包体并从出厂副本还原被改坏的文件）；修不了的（程序文件缺失）列出具体文件名并让人重装，不假装修好；会断开机器人的动作（重置面板口令）一律留给用户自己点 |
| `build/make_factory.py` | 往包里落"离线自救副本"：两个 exe + 图标/公钥/WebView2 dll 的出厂副本、NapCat v4.18.41 两个压缩包（上游许可允许随全文再分发，全文一并落盘）、4138 个文件的逐文件 SHA-256 清单。摘要只有一个真源——从 `AstrBotOneBotInstall.cs` 里读，不复制粘贴（手抄错过一位，被自己的闸门拦下）。代价是安装包 140 → ~200 MB |
| `test/*.py` | 验收套件 |

### 界面为什么不会「未响应」

启动器早期版本在人反馈「容易未响应」时会卡住，根因不是核心，而是界面自己：核心启动/重启时会连发
几百行日志，老写法是**每行一次 `BeginInvoke` + `AppendText` + `ScrollToCaret`**，等于把这些操作排成
几百条独立消息压在消息循环上。同一窗口里实测：灌 1200 行时，投一条消息到它真被执行之间延迟
**2516 ms**（这段时间窗口就是"未响应"）。现在改成生产者只入队、UI 侧每 200 ms 批量写一次并按行数裁头，
同一实验实测 **0 ms**。同理，客户端端口探活与面板 HTTP 也全部挪出 UI 线程，并有断言守着：
把面板换成"连得上但永远不回话"的黑洞端口，连撞三次轮询后消息循环延迟仍是 0 ms，且在飞的探测只做一次。

改完还被人反馈卡，这一轮就不猜了，加了两件能自证的东西：

1. **构建戳**：`build/make_stamp.py` 每次编译前把源码指纹（九个 launcher 源文件 + `astrbot_desktop.py`
   + `README.txt` 的 sha256 前 8 位）写进 `launcher/AstrBotStamp.cs`，标题栏、诊断包、自检输出都带着它。
   「您跑的到底是哪一份」从此是一个可比的答案，而不是靠猜。
2. **UI 线程 I/O 抓奸器**：可能在这条线程上做网络、起进程、扫目录的位置都调 `UiGuard.Note(...)`；
   它不改行为只留证据，诊断包里会出现「UI 线程上做过的 I/O：…」这一行。`--uitest` 的最后一条断言
   就是"这一路跑下来没人偷在 UI 线程上做 I/O"。它当场抓出三处，全部修成后台：
   · 一次性通道 `RunOnce`（要等核心子进程跑完，几十秒都可能）；
   · 状态栏每三秒的面板端口探活（改成读后台缓存的在线状态，不在 UI 线程连端口）；
   · **启动时扫描客户端目录**（走 Program Files、桌面与各盘根，冷盘上就是几秒白屏）。
   第三处构建机上根本测不出来——那台机器没装 NapCat，扫描早早返回空；装了客户端的机器才会撞上，
   这正是"我这里好好的"的漏洞所在。现在点「启动」若还在后台扫描，会先说明"正在找，找到就自动启动"，
   不拿弹窗把人堵死。

## 二、运行时行为改动（都在我们自己的进程里，不改上游文件）

入口 `build/astrbot_desktop.py` 在导入 AstrBot 之后、启动之前，对第三方库 `aiocqhttp`
做了两处运行时替换，目的是修 OneBot/NapCat 重连后"看着在线、永远不回消息"：

1. **注销反向 WebSocket 的 API 通道时按连接对象判断**，不再只按 `self_id` 删表项。
   原行为：旧连接（被系统冻结的那条）晚一步死掉时，会把新连接的登记一起删掉。
2. **新连接到达时主动关闭旧连接**，而不是等 TCP 自己超时（手机上那几分钟机器人是聋的）。

实现按接口查找目标类（同时存在 `_add_wsr_api_client` 与 `_remove_wsr_api_client` 的类），
不硬编类名；任何异常都只打印 warning，绝不阻断启动。
可用 `ASTRBOT_DISABLE_SESSION_PATCH=1` 关闭，`--self-check` 的 `[OneBot 会话恢复]` 一节会报补丁来源。

## 三、默认值改动（桌面产品姿态，不改上游代码）

| 项 | 上游 | 本构建 | 原因 |
|---|---|---|---|
| 面板监听 | 可配 | 默认 `127.0.0.1` | 桌面机不该把管理面板暴露到局域网 |
| 指标上报 | 开 | 关（`ASTRBOT_DISABLE_METRICS=1`） | 不在别人机器上悄悄上报 |
| 面板内「更新核心」 | 可用 | 拒绝（`ASTRBOT_DESKTOP_MANAGED=1`） | 自更新会往冻结包里塞散 .py，必坏 |
| 插件依赖安装位置 | 环境 site-packages | `data\site-packages`（`ASTRBOT_DESKTOP_CLIENT=1`） | 不污染程序本体，保持便携 |
| `log_file_enable` | false | 首次启动播种为 true | 出问题时有东西可发 |
| `pypi_index_url` | 上游当时的默认 | 首次启动钉为阿里云镜像 | 实测本机 pypi.org 11.3 s vs 镜像 0.31 s |
| 插件仓库下载 | 需手填代理 | 默认加国内加速前缀，失败自动回退直连 | 实测本机直连 GitHub 分支归档 21 s 拿不到字节；`data\desktop.conf` 里 `plugin_mirror=` 可换可关 |

## 四、冻结打包必须做的三件事（否则功能就是坏的）

1. **pip 的 vendored CA 证书要落盘**：AstrBot 在冻结进程内调 pip，pip 按路径读
   `pip/_vendor/certifi/cacert.pem`，PyInstaller 默认只把它打进字节码 → 装插件依赖时
   `FileNotFoundError`。修：收集 pip/setuptools/distlib 数据文件 + 入口设
   `PIP_CERT` / `REQUESTS_CA_BUNDLE` / `SSL_CERT_FILE`。
2. **所有已安装分发的 `*.dist-info` 元数据必须进包**：AstrBot 用 `importlib.metadata`
   判断"核心是否已有该依赖"，查不到就重复安装，而它的运行时冲突保护会直接拒绝加载插件
   （`RuntimeError: A conflict between plugin dependencies ... charset_normalizer`）。
   这条是"插件功能不能用"的真正根因。
3. **内置插件的 `main.py` 要以散文件落盘**：`star_manager` 是按磁盘上的 `main.py` 找插件的，
   只进 PYZ 字节码会被静默跳过。

## 五、签名

私钥只存在于构建机的 Windows 证书库，不入库、不随包走；随包走的是公钥
`keys/astrbot-signing.cer`。签名带 RFC3161 时间戳，证书过期后仍可校验。
`build/signature_proof.ps1` 会临时把公钥装进当前用户信任库、改一个字节验证会报
`HashMismatch`、然后立刻撤销——机器上不留改动。
