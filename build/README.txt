AstrBot 桌面版（Windows）
========================

版本：AstrBot v4.29.3（WebUI v4.29.3）· 内置 Python 3.12.10 · 无需安装，整个文件夹拷走就能用

这一版没有黑色命令行窗口。双击 AstrBot.exe 出现的是程序自己的窗口，机器人在后台跑；
点窗口右上角的叉只是缩到右下角托盘（机器人继续收发），要真停请在托盘图标上右键「退出 AstrBot」。


一、目录结构
-----------
AstrBot.exe       唯一需要点的程序：原生启动器（约 66KB）
core\             AstrBot 本体（AstrBotCore.exe + _internal），不用单独双击
data\             第一次启动后自动生成：配置、数据库、日志、知识库、插件、内嵌浏览器缓存都在这
ffmpeg.exe        QQ 语音与音频格式转换用
runtime\          便携 Python 3.12，只在给插件装依赖时用到（见第七节）
WebView2Loader.dll + Microsoft.Web.WebView2.*.dll   内嵌面板用，别删（缺了会自动退回浏览器）
astrbot.ico/png   图标
README.txt        本文件


二、第一次启动（给使用者的就三步）
--------------------------------
1) 双击 AstrBot.exe。窗口右上角状态灯从「启动中」变绿「运行中」就算就绪；
   「启动后自动开面板」是勾着的，就绪后面板会**直接在这个窗口里打开**（内置面板页签），不用另开浏览器。
2) 面板登录：用户名 astrbot，密码在「概览」页左下的「首次登录密码」卡片里，点复制再粘过去。进去后请先改密码。
   面板自己的主题（深/浅）在面板右上角切，和启动器的主题互不影响。
3) 接 QQ：切回「概览」页，右侧「接入 QQ」选类型、填参数，点「保存并连接」。程序自己写配置并重启，
   连上后「QQ 状态」会变绿，并显示已收到的消息条数与最近一条内容。

模型（LLM）在面板里配：提供商 → 添加，OpenAI 兼容格式填 base_url 和 key 即可。


三、四种 QQ 接法
---------------
· OneBot v11 —— 个人号（NapCat / Lagrange / go-cqhttp）
    启动器里填：监听地址 127.0.0.1、端口 6199、连接令牌（自己定）
    NapCat 侧新建「Websocket（反向）」：URL = ws://127.0.0.1:6199/ws ，令牌填同一个
      这一段现在不用手抄：面板连上以后，启动器会把 AstrBot 侧的表单替他填好（含随机令牌），
      并把 URL 与令牌复制到剪贴板，剩下就是在 NapCat 面板里粘贴一次。
    面板口令（v4.18.41 实测）：客户端面板的路由全部挂在 /api 下，而且配置文件里
    **已经没有明文令牌**了——只有口令的哈希与盐。想问"登录到哪一步"、想拿二维码，
    必须拿面板口令去 POST /api/auth/login（body 里是 sha256(口令 + ".napcat") 的十六进制）
    换一张一小时有效的 Credential，之后每次请求带 Authorization: Bearer <Credential>。
    所以右侧那一栏多了「NapCat 面板口令」+「连接面板」：口令只留在本机 desktop.conf 与内存里，
    不进日志、不进诊断包（这条有断言守着，而且写桩脚本时因为 bat 带 BOM 被 cmd 回显过一次，
    当场被这条断言抓住）。口令不对会说"面板口令不对"，被限流会说"登录太频繁"，
    拿不到凭证时状态行写"面板：要口令"——绝不把没拿到显示成已登录。
    注意：这条反向连接必须带 X-Client-Role: universal 与 X-Self-ID 两个请求头。
    aiocqhttp 用 X-Self-ID 登记 API 通道，缺它会直接握手失败（HTTP 400）。
    NapCat / go-cqhttp 默认都会带；自己写客户端时别漏。令牌走 Authorization: Bearer 头，不是 URL 参数。

    这条路上的客户端，启动器现在也能一起管（不用您再去开控制台盯它）：
      · 概览页下方「QQ 接入客户端（个人号）」一段：自动发现本机已装好的 NapCat
        （Shell 版认 NapCatWinBootMain.exe + napcat.mjs，Framework 版认 napimain.exe + napiloader.dll；
        解压多套一层目录也找得到；只看到目录名不算找到，避免认错别人家的文件夹）。
      · 「启动客户端 / 停止」直接调客户端自带的启动脚本，不重抄它的引导过程——它要读注册表找 QQ.exe、
        要设自己的环境变量、Shell 版还要管理员授权，我们照着拼一遍迟早对不上版本。
        停止只按我们起的进程树 PID 杀，绝不按名字乱杀 QQ.exe（那可能是您正在用的窗口）。
      · 三段状态分开显示：客户端进程 / QQ 账号登录到哪一步 / AstrBot 有没有连上。
        这三件是不同的事，合在一起说「已连接」会骗人。
      · 「登录二维码」把客户端自己出的登录码取回来（读它 config\webui.json 里的端口与令牌，
        问它的 /GetQQLoginQrcode），画成图片显示在本程序的窗口里；拿不到就自动打开它的面板页，不卡住。
      · 勾上「跟着一起起」后，客户端面板端口掉线会按次数自动重试（有上限，不会无限重启）。
      · 「下载 NapCat」是可选的：版本固定在 v4.18.41，下载回来先按写死的 SHA-256 逐字节核对，
        对不上就**拒绝安装**（也不回退到别的来源——内容对不上时"能装上"是坏事）；
        国内优先走加速前缀，失败自动退回直连。装到 data\onebot\ 下，随包落一份「来源与许可说明.txt」，
        删掉那个目录就等于卸载。绝不会出现"自己悄悄装上了"。
      · 这个下载器是整个 exe 里唯一直接用 HTTPS 的地方，也就撞上了一个只有 csc 构建才会有的坑：
        程序集没有 TargetFrameworkAttribute，.NET 按 4.0 的默认值只给 SSL3/TLS1.0，现代端点直接拒握手
        （表现就是「下不到客户端压缩包：无法连接到远程服务器」）。Program 入口现在显式打开 TLS1.2
        （Win10 起再带 TLS1.3），并把这条钉进 --onebottest：断言「入口打开了 TLS1.2」+ 真对线上释放页
        问一次 HEAD（拿得到就算通过，本机断网时显式记 NOTRUN，不假装通过也不算红）。
        报错文案会说实话并给三条出路：把代理开起来重点一次 / 浏览器自己下 v4.18.41 那个包再点「选目录」/
        先走官方机器人扫码那条不需要客户端的路。摘要不对的包一律拒装，从哪条路拿来的都一样。
    两种客户端都要求本机装过 QQ 客户端（NapCat 从注册表拿 QQ.exe 才能工作）。启动器把它写进了自检：
    AstrBot.exe --self-check 里有一节 [QQ 接入客户端]，会告诉您是"没装客户端"还是"客户端没起来"还是"号没登录"。
    丑话写在前面：这类客户端能读该 QQ 号的全部会话，个人号跑机器人违反腾讯服务条款，被限制或封号的风险由使用者承担。

· QQ 官方机器人（Websocket，推荐）—— 启动器里点「扫码登录」就行，不用碰密钥
    拿密钥有两条路：
      A. 启动器右侧点「扫码登录」→ 弹出二维码 → 用手机 QQ 扫 → 确认授权哪个机器人。
         AppID 和 AppSecret 由核心拿到并**直接写进 data\cmd_config.json**，全程不显示在日志里、
         不进诊断包、也不用登录面板（面板接口要先有密码，而初始密码多半已经被改过）。
         写完后启动器自动重启核心，状态灯变绿就是连上了。
         扫码进程还有一条兜底：二维码看不清时点「用浏览器打开授权页」，那是同一个一次性任务。
      B. 在 q.qq.com 后台抄 AppID / AppSecret，填进启动器右侧表单点「保存并连接」（这条会重启核心）。
    扫完码想在启动器里看到/改这两个值：重启一次启动器就会从配置里回填。
    其它三种接法（OneBot v11 / Webhook / Satori）没有这种扫码登录——参数本来就要在 NapCat 或
    公网回调那边拿，按钮在那三种情况下会直接把您送到面板的创建页，不把人堵死。
· QQ 官方机器人（Webhook）—— 需要公网能回调到 /api/platform/webhook/{uuid}
· Satori —— NapCat 的 Satori 协议出口，填事件地址与 API 地址

表单会挡常见错误：AppID 必须是纯数字、AppSecret 不能空、端口要在 1-65535、Satori 地址必须以
ws:// 或 wss:// 开头、标识只能用字母数字下划线短横。

状态灯认识各家的"连上了"文案（它们并不统一）：OneBot v11 打「适配器已连接」，Satori 打
「连接成功 - Bot ...」并会把 Bot 号显示出来，QQ 官方机器人只打「Websocket session starting」。
连不上时状态行变红并显示最近一条报错，鼠标停在上面可以看全文。

个人号想跑在别的机器上：把监听地址改成 0.0.0.0 并放行防火墙，同时务必设强令牌并改密码。


四、窗口里的东西
---------------
顶部两个页签：「概览」是状态灯、密码卡片、运行控制按钮和接入 QQ 的表单；「内置面板」把 AstrBot 的
WebUI 直接嵌在窗口里（高级功能——知识库、插件、定时任务、MCP、人设、日志、配置文件编辑——全在里面）。
页签的选择会被记住，下次启动停在同一个页面。

按钮：打开管理面板（=切到内置面板）/ 启动·停止 / 重启服务 / 打开数据目录 / 打包自检 / 导出诊断包
右侧表单里：保存并连接 · 断开这个平台 · 扫码登录（官方机器人是扫码，其余三种把您送到面板创建页）
右侧表单下面「QQ 接入客户端（个人号）」：QQ 号（留空=扫码登录）· 选目录 · 启动客户端 · 停止 ·
  登录二维码 · 下载 NapCat，旁边一行状态分开写着 客户端 / QQ 登录到哪一步 / AstrBot 有没有连上
勾选：开机自动启动（写当前用户的运行键，静默 --minimized 启动）· 启动后自动开面板 · 允许局域网访问
底部：实时运行日志。同样的内容会写到 data\logs\astrbot.log（20MB 轮转），出问题时把这份文件发出来。

内嵌面板的容错：核心比面板慢几秒是常态，失败卡片会在核心就绪时自动重导；也可以点「重试」。
这台机器没有 WebView2 运行时（或初始化失败）时，会自动退回系统浏览器，并显示「改用浏览器打开」按钮，
不会白屏。想在浏览器里用：托盘右键 →「在浏览器中打开」。


五、不用界面也能做（命令行）
--------------------------
AstrBot.exe --minimized                     静默启动到托盘
AstrBot.exe --self-check                    体检结果弹窗（或 --self-check-to=文件 输出到文件）
core\AstrBotCore.exe --self-check           直接把体检结果打印出来
core\AstrBotCore.exe --reset-password       重新生成并打印面板初始密码
core\AstrBotCore.exe --host 0.0.0.0 --port 6185 --no-browser   直接跑核心
core\AstrBotCore.exe --platform-file  x.ini 写入一个平台配置块（启动器「保存并连接」走的就是它）
core\AstrBotCore.exe --platform-dump  x.ini 导出当前所有平台配置
core\AstrBotCore.exe --login-bind qq_official --bind-name "QQ 机器人" --bind-timeout 300
        扫码登录（启动器「扫码登录」按钮走的就是它）：往 stdout 打 BIND_* 行
        BIND_QR_URL / BIND_QR_FILE（二维码 PNG 落在 data\tmp\）/ BIND_STATE pending /
        BIND_DONE id=... / BIND_FAIL <原因>。密钥只在进程内部，不落 stdout。
core\AstrBotCore.exe --login-bind weixin_oc
        微信客服也是扫码，同一套协议行（这个类型的界面入口目前在面板里）。

x.ini 是 UTF-8 的块格式，改完重启核心生效：

  [platform]
  type=aiocqhttp
  id=qq_onebot
  name=QQ 个人号
  enable=true
  ws_reverse_host=127.0.0.1
  ws_reverse_port=6199
  ws_reverse_token=你的令牌
  [/platform]

  其它类型的键：
  QQ 官方机器人     type=qq_official            + appid / secret / enable_group_c2c
  QQ 官方(Webhook)  type=qq_official_webhook    + appid / secret / port
  Satori            type=satori                 + satori_endpoint / satori_api_base_url / satori_token
  想停掉某个平台：同 id 再写一次，把 enable 改成 false。


六、默认安全姿态
---------------
· 面板只监听 127.0.0.1（上游默认是 0.0.0.0）。勾「允许局域网访问」才对外，勾之前请先改密码。
· 指标上报关闭（ASTRBOT_DISABLE_METRICS=1）。
· 面板里的「更新核心」被禁用：EXE 是自包含的，覆盖内部 .py 会直接把程序打坏。
· 面板密码在 data\cmd_config.json 里是 pbkdf2 哈希，不是明文。
· 落盘日志默认开启（上游默认关闭），路径 data\logs\astrbot.log。
· 启动时 AstrBot 会去拉一次官方文生图端点和 models.dev 的模型元数据（上游自带行为，用来填模型下拉框）。
  完全离线的机器上这两条会变成 warning，不影响文字收发。
· 同一份程序重复双击会被单实例锁拦住，不会起两份抢端口。


七、插件与插件依赖
----------------
插件市场安装、依赖自动安装在这个包里是**可用的**（早期版本不行，原因见下）。

AstrBot 在冻结的 EXE 里是进程内调用 pip，而 pip 会按路径读它自带的 CA 证书文件；PyInstaller 默认只把
该文件打进字节码、磁盘上没有，于是装依赖时在 truststore 里报 FileNotFoundError。现在打包时：
  · 把 pip / setuptools / distlib 的数据文件收进包里（pip 自己那份 cacert.pem 落盘）
  · 入口把 PIP_CERT / REQUESTS_CA_BUNDLE / SSL_CERT_FILE 指向磁盘上真实存在的证书包
  · 开启 AstrBot 的桌面模式，插件依赖装进 data\site-packages（不会污染程序本体）
  · 把**所有**已安装分发的 dist-info 元数据打进包（这条很关键）

最后一条为什么关键：AstrBot 靠 importlib.metadata 判断"插件要的依赖核心里有没有"。冻结包里若查不到
某个包的 dist-info，它就以为没装，于是给插件重复装一份；而 AstrBot 有个保护——插件依赖与运行时核心模块
同名就拒绝加载插件，结果就是报
  RuntimeError: A conflict between plugin dependencies and the current runtime ... charset_normalizer
表现为"插件装不上/用不了"。补齐元数据后，核心已有的依赖不会被重复安装，冲突自然消失。

验证方法：启动器点「打包自检」，或命令行跑
    core\AstrBotCore.exe --self-check
看这两节：
    [插件下载源]
      OK   插件依赖 PyPI 源: https://mirrors.aliyun.com/pypi/simple/
      OK   插件仓库加速前缀: https://gh-proxy.com（失败自动回退直连；desktop.conf 里 plugin_mirror= 可换可关）
      ...  pip 试装（走一次索引往返，等价于装插件依赖）
    [插件依赖安装]
      OK   pip 包管理器
      OK   pip 的 CA 证书包: ...\core\_internal\certifi\cacert.pem
      OK   桌面模式（插件依赖装进 data\site-packages）: True
      OK   pip 试装通过，插件依赖可以正常安装
最后一行是真跑了一次索引往返，用的就是上面那个 PyPI 源，等价于装插件依赖。

下载默认走国内能通的源（这条是产品姿态，不是可选项）：
  · 插件依赖：PyPI 源钉在 https://mirrors.aliyun.com/pypi/simple/
    （实测本机 pypi.org 11.3 秒、这个 0.31 秒；面板「高级设置」里同名项可以改，改了不会被覆盖）
  · 按 GitHub 仓库地址装/更新插件：默认加国内加速前缀 https://gh-proxy.com
    （实测本机直连 GitHub 分支归档 21 秒拿不到字节，走前缀 1.9~2.7 秒，且与直连的归档**逐字节一致**；
    加速源哪天挂了会自动回退直连，不会连带插件装不上；面板里自己填了代理就以您填的为准）
    要换别的加速源或干脆直连：编辑 data\desktop.conf
      plugin_mirror=https://别的前缀/     换成别的
      plugin_mirror=                      留空 = 直连 GitHub
  · 插件市场的 zip 是 AstrBot 官方对象存储的地址，本来就是整条链路里最快的一步，不走上面的前缀
    （前缀只代理 github.com 的归档，套上去反而会坏）。

万一还是装不上，可以手动补依赖（AstrBot 装依赖前会先预检，依赖齐了就跳过 pip）：
    runtime\python.exe -m pip install --target data\site-packages -i https://mirrors.aliyun.com/pypi/simple/ 插件要求的包名
然后点「重启服务」。带 .pyd 的编译包要挑 cp312、win_amd64 的 wheel（运行时就是 3.12）。


八、这一版验证过什么
------------------
一条命令跑全套（在构建工程目录里）：

    venv\Scripts\python.exe test\run_all.py

14 步、286 项断言，全部对着**打包后的产物**跑（不是源码目录），所以绿的是您手里这份程序。
  （其中「插件下载国内源」那一步里有 1 项要靠外网基线做逐字节比对，加速源或直连不通时它会少 1 项并
    显式打成 NOTRUN，不会悄悄算通过；其余 285 项与本网络无关。）
每一步都是往 stdout 打 PASS/FAIL 行、由 run_all 汇总；任何一步红了都会把那几行原文贴出来。

  步骤                                     断言  验的是什么
  核心自检 --self-check                      28   依赖收全、端口空闲、CA 证书真能发起一次 pip 试装、
                                                      国内源与扫码登录通道都在位
  界面自检 --uitest                          79   不靠鼠标自动化：exe 内的钩子逐项断言控件状态 + PrintWindow
                                                      截图。四种 QQ 表单渲染且可见、平台块往返、状态灯颜色、
                                                      密码卡片、页签与设置记忆、内嵌面板真加载完成、
                                                      启动/手动停止的状态机、纠错与扫码登录的界面出口；
                                                      再加卡顿回归（灌 1200 行、黑洞端口轮询、故意堵 950 ms）
                                                      与「标题栏带构建戳 / 这一路没人偷在 UI 线程上做 I/O」
  核心验收 verify_app                        12   含 PE 子系统检查：启动器与核心都是 GUI 子系统，
                                                      结构上不可能再弹命令行窗口
  双击级验收 verify_desktop_app              10   从空 data 目录起步：启动器拉起核心 → 面板 200 →
                                                      6199 只监听 127.0.0.1 → mock OneBot 客户端推进 /help
                                                      → 收到 AstrBot 回发的 send_private_msg → 进程树里没有
                                                      conhost、退出后不留残留
  面板接口冒烟 probe_panel_api               20   登录、平台列表、插件列表、配置读写这几条真实入口能用
  插件依赖离线验收                            5   依赖预检在断网时给的是人话，不是崩
  插件下载国内源验收                         14   默认镜像与 GitHub 直连**逐字节一致**（sha 相同）、
                                                      加速前缀失败自动回退直连、desktop.conf 一行可关、
                                                      真按仓库地址装成一个插件并确认走的是镜像
  签名验收 verify_signature                  11  启动器、核心、安装程序三个文件的签名链可验、时间戳在、
                                                      被改一个字节后立刻判为签名不匹配（不是「看不出被改」）
  启动器纠错验收 verify_recovery             41   注入真故障：端口被占→自动换端口、外部 taskkill 核心→
                                                      退避重启并恢复运行、cmd_config 写成非法 JSON→
                                                      改名留档并用默认值重建、诊断包里的密码/密钥/token
                                                      全部变成 ***
  内置扫码登录验收 verify_qq_bind            13   向 q.qq.com 真建了一个绑定任务、二维码 PNG 落盘并显示、
                                                      轮询与超时都讲人话；并确认密钥不会出现在 stdout、
                                                      失败时不会把半成品写进配置
  OneBot 重连接管验收 verify_onebot_recovery   3   旧连接僵死不放手时：新连接能被接受、旧连接死掉后
                                                      新连接仍然收得到 API 调用（就是「重连上但再也听不到消息」
                                                      那个病的红绿对照）
  面板上传插件验收 verify_plugin_upload        7   手机/面板那条唯一可行的路：上传 zip → 落进 data\plugins →
                                                      插件列表里有 → 重启后日志里有它自己打的确认行 →
                                                      且整份日志没有 Failed to load plugin
  QQ 接入客户端验收 --onebottest              27   拿一个假客户端把个人号那条新链路真跑一遍：按标志文件认出
                                                      NapCat（Shell / Framework 两种，解压多套一层也认得）、
                                                      读它自己的 webui.json 拿端口与令牌（令牌不落日志）、
                                                      调它的启动脚本、用端口判活而不是看脚本返回值、
                                                      取回登录二维码并让核心画成 PNG、登录状态问到 online、
                                                      停止后端口不再响应且没有残留进程；
                                                      本地备着已核对摘要的 NapCat 包时再验字节闸门：
                                                      装完能被发现、随包落来源说明、解压挡得住路径穿越、
                                                      改坏一个字节必须拒装；
                                                      断网那条也真跑：不传 localZip，只从随包的
                                                      factory\napcat\ 副本装一遍，装完仍按摘要核对
  一键修复验收 --repairtest                    17   先注入真故障再让修复器上：坏配置、缺目录、缺程序文件、
                                                      被占的端口、没人管的孤儿核心、被改坏一个字节 png；
                                                      断言"改坏之前确实坏了"（不注入就证明不了这条有用）、
                                                      修复后每项要么复验通过、要么如实标成需要用户处理，
                                                      还断言修复器没在 UI 线程上做 I/O；
                                                      两层冗余都真验：藏掉出厂副本再删一个文件，断言它能从免安装包按摘要补回来

· 「容易未响应」的成因与回归：日志以前是**每行一次 BeginInvoke + AppendText + ScrollToCaret**，
  核心启动/重启时连发几百行就把消息循环占满了。同一窗口里实测：灌 1200 行时，投一条消息到它真被执行
  之间延迟 2516 ms（这段时间窗口就显示为「未响应」）；现在生产者只入队、界面每 200 ms 批量写一次并按
  行数裁头，同样洪泛实测 0 ms。客户端的端口探活与面板问答也全部挪出 UI 线程——把面板换成「连得上但
  永远不回话」的黑洞端口，连撞三次轮询，界面延迟仍是 0 ms，且在飞的探测只做一次。这两条都写进了
  界面自检，以后谁再改回去，套件当场就红。
· 上一轮改完还是有人说卡，所以这轮不再靠猜：① 构建戳（build\make_stamp.py 把源码指纹写进
  AstrBotStamp.cs，标题栏、诊断包、自检输出都带着它——"您跑的到底是哪一份"从此有一个可比的答案）；
  ② UI 线程 I/O 抓奸器（launcher\AstrBotStamp.cs 的 UiGuard）：凡是可能在这条线程上做网络、
  起进程、扫目录的地方都留一句 Note，诊断包里会原样列出。界面自检最后一条断言就是"这一路跑下来
  没人偷在 UI 线程上做 I/O"——它当场抓出三处：一次性通道（等核心子进程，几十秒都可能）、
  面板端口探活、以及**启动时扫客户端目录**（走 Program Files、桌面和各盘根，冷盘上就是几秒白屏，
  也是这台机器没有 NapCat 时测不出、装上就卡的那一个）。三处现在全部挪到后台线程，
  找客户端改成异步：点「启动」时如果还在扫，会先说"正在后台找，找到就自动启动"，不拿弹窗堵人。
· 一键修复（左列那个按钮，launcher\AstrBotRepair.cs）：九项检查按「检测 → 确认坏了才动手 → 动手后复验」跑，
  整段在后台线程上做（点下去界面不卡，抓奸器也断言了这一点）。有把握的才自动修：补建缺失目录、把读坏的
  cmd_config.json 留档挪开、端口被占就换并写回设置、停掉没人管的孤儿核心（只认"路径就是我们这份核心、
  且现场没有别的启动器在管它"的进程，按 PID 精确杀）、核心没起或半死就重启我们那一份、按清单核对包体并
  从出厂副本还原被改坏/被删的文件。修不了的绝不说成修好了：清单里对不上、而 factory 又没带副本的，
  会列出具体文件名让人重装（安装程序不动 data\）；正在运行的那个 AstrBot.exe 不能覆盖，就先把它改名挪开
  再把出厂副本放回原位（坏的那份留在原地给人看），并说明"下次启动才生效"。重置面板口令这种会断开机器人
  3~5 秒的动作，一律留给用户自己点。
· 冗余分两层（build\make_factory.py 落的 factory\，签名之后跑）：
  第一层·随包副本 —— 两个 exe（签名后的字节）+ 图标/公钥/WebView2 那几个小文件的出厂副本，
    加上 NapCat v4.18.41 两个压缩包（34.2 + 35.0 MB；上游许可明文允许再分发，条件是随包带许可全文与
    来源且不得商用，全文落在 factory\LICENSE-NapCat.txt），再加上 4138 个文件的逐文件 SHA-256 清单。
    「下载 NapCat」优先用这个副本，断网、代理没开、GitHub 拿不全字节都照样能装，且仍然按摘要核对。
  第二层·免安装包 —— 那份 AstrBot-4.29.3-desktop-portable.zip 本身就是这套程序的完整副本。
    修复器会在启动器同目录、上级目录和"下载/桌面"里找它，找不到也可以点「从免安装包还原…」直接指给它；
    清单里对不上而第一层没覆盖到的文件，就从这里按摘要抽出来补回去（只读那个 zip，不改它）。
  代价是安装包 140 MB → ~230 MB。摘要只有一个真源：make_factory.py 从 AstrBotOneBotInstall.cs 里读，
  不复制粘贴（我第一版手抄错了一位，被自己的闸门拦下来了）。
· 验收套件不许拿"正在服役的目录"当靶子（2026-10-30 出的真事故）：verify_desktop_app 要从空 data 起步，
  一上来就 rmtree(data\)；那次被测的 dist-new\AstrBot 恰好是使用者自己在用的目录，里面装着他真在跑的
  NapCat 客户端，结果 data\onebot\NapCat.Shell 被删到只剩 8 个文件（正在运行的进程占住了那几个）。
  按固定摘要把 680 个文件写回去修好了（整包 SHA-256 先核对、zip-slip 守卫、占用中的跳过、落来源说明）。
  现在两处闸门：run_all 见到 data\onebot|plugins|config 就默认拒绝（明知要用 --allow-live-data），
  verify_desktop_app 删不动就直接退出码 2 说明原因，不再半删。整套验收一律对着
  **免安装包解出来的干净副本**跑：
      powershell -Command "Expand-Archive installer\AstrBot-4.29.3-desktop-portable.zip dist-verify"
      venv\Scripts\python.exe test\run_all.py --app dist-verify\AstrBot
  这样既不会碰任何人的数据，测的也正是别人手里那份。
· 外网那几条要分清"产品坏了"和"这台机器出不去"：连不上（HTTP 码 0）或代理在做 TLS 中间人
  （链上是自签证书，报 CERTIFICATE_VERIFY_FAILED）时，显式记 NOTRUN 并写明原因；连上了却拿到坏包、
  字节与 GitHub 不一致，才留 FAIL。以前一律算红，结果代理一开就三处假红，把真问题淹掉了。
· 交付物另有两道独立审计：test\audit_personal_info.py（成品与已发布源码里的身份指纹：账户名、
  计算机名、账号、域名、服务器 IP、邮箱手机号、令牌、私钥、凭据真值、使用者数据文件；厂商二进制
  单独定罪，扫描器自己还要 --self-test 证明模式抓得到、对第三方噪声不误报），
  以及 build\audit_package.py（使用者数据与凭据）。
· 发布后还有匿名验收 test\verify_published_repo.py：不带 token 从 GitHub 拉 zipball，与本地
  staging 逐字节比对、核对每个文件的 blob 哈希、核对资产的远端 sha256 与前 4 KB 内容。
· 安装包另算 19 项（test\verify_installer.py，装完再卸载一次）：简中/繁中/英文三套文案、
  装到当前用户目录不需要管理员、安装前不会碰已有 data\、卸载默认保留 data\、
  控制面板「添加或删除程序」里的条目名称与卸载命令干净。
· 最后一步是「测你真拿到的那份」：把 AstrBot-Setup-4.29.3.exe 静默装进一个临时目录，
  把上面那条「双击级验收」再对着**装完的目录**跑一遍（10 项全绿，含 mock OneBot 推进 /help 真收到回包），
  并核对装完的两个 exe 与构建产物逐字节一致、签名和时间戳都还在，然后卸掉、删掉临时目录。

· 打包过程中被这套验收抓出来并修掉的坑：
  aiosqlite 被 SQLAlchemy 按点号名字延迟导入而漏收（数据库起不来）、
  内置插件的 main.py 只进字节码没落盘（插件被静默跳过）、
  日志重定向到文件时 GBK 编码失败把初始密码横幅吞了、
  子进程（ffmpeg 等）在无控制台父进程下会闪黑框、
  pip 的 vendored CA 证书没落盘 → 插件依赖装不上（用户实测日志抓到的；
  现在 --self-check 会真跑一次 pip 试装盯住它）、
  纠错里的端口探测单靠 bind 或单靠 connect 都会说谎（一个非接受型监听能让端口看着是空的，
  一个被拒的 connect 又能让空闲端口看着是占的），现在三条判据一起用


九、启动器会自己纠错
-----------------
出问题不用先找我，启动器已经做了四件事：

1. 启动前体检：面板端口被别的程序占了，自动往后找一个空闲端口，并把新端口记进
   data\desktop.conf（面板地址、内嵌面板都跟着走）；想固定端口就改那一行。
2. 看门狗：核心进程活着但面板 120 秒还没开始监听，判定启动失败，杀掉重来，
   不用对着一个「一直在启动」的界面干等。
3. 崩溃自愈：核心意外退出（不是您点停止）按 3 秒 / 10 秒 / 30 秒退避自动重启，
   最多 3 次，状态灯显示「自动恢复中 1/3」；重新起来后额度自动补满。
   能就地修的先修：配置文件被存成非法 JSON 时，把它改名成 cmd_config.bad-日期.json
   留着，让核心用默认配置重建——旧内容一个字节都没丢，要找回 QQ / 插件配置，
   把备份里的内容复制回新的 cmd_config.json 即可。
   动手前会先确认那个文件真的不是合法 JSON：插件自己解析缓存报的 JSON 错，不会碰您的配置。
4. 说人话：报错会分成「端口被占用 / 配置文件损坏 / 目录不可写 / 插件依赖冲突 /
   插件缺依赖 / 启动超时 / 核心异常退出」，每种给一句能照做的建议。
   自动恢复兜得住就不弹框；兜不住才弹，弹框里保留「重试」。

导出诊断包（左下一个按钮）：把「自检输出 + 日志尾部 + 当前状态」写成
data\diagnostics\astrbot-diag-日期时间.txt。密码 / 密钥 / token 一律替换成 ***，
可以直接发给别人定位问题，不用截图。

需要动手的只有两类：插件缺依赖、目录不可写——自动重启也解决不了，
启动器会把命令直接给您（例如 runtime\python.exe -m pip install --target data\site-packages 包名）。


十、升级 / 重新打包
-----------------
构建工程目录（下面路径都相对它，放哪都一样）。**顺序是有原因的**，跳步会发出没签名的包：

    1) venv\Scripts\python.exe -m PyInstaller --noconfirm --distpath dist-new build\AstrBot.spec
       --distpath 不能省：写进 dist-new（一次性的组装目录），不要往任何带 data\ 的目录里打。
       package_dist.py 现在有闸门：目标目录里存在 data\ 就拒绝运行（要显式 --allow-data 才继续）。
    2) venv\Scripts\python.exe build\make_stamp.py             # 把源码指纹写成构建戳（编译前跑，标题栏/诊断包靠它回答"您跑的是哪份"）
       启动器（界面）用系统自带 csc 编译，九个源文件 + WebView2 两个托管程序集一起编
       （System.Web.Extensions 给 JavaScriptSerializer 读 JSON 用，System.IO.Compression 给客户端下载解压用）：
       C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
         /out:build\launcher\AstrBot.exe /win32icon:build\assets\astrbot.ico ^
         /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
         /reference:System.Web.Extensions.dll ^
         /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
         /reference:build\vendor\webview2\Microsoft.Web.WebView2.Core.dll ^
         /reference:build\vendor\webview2\Microsoft.Web.WebView2.WinForms.dll ^
         launcher\AstrBotLauncher.cs launcher\AstrBotControls.cs ^
         launcher\AstrBotPanelView.cs launcher\AstrBotLauncherUi.cs ^
         launcher\AstrBotRecovery.cs launcher\AstrBotBind.cs ^
         launcher\AstrBotOneBot.cs launcher\AstrBotOneBotInstall.cs ^
         launcher\AstrBotStamp.cs
       嫌这行长可以照 build\launcher\csc-sources.rsp 跑：csc @build\launcher\csc-sources.rsp
       （rsp 里的路径相对构建工程根目录，必须在根目录下执行）
    3) venv\Scripts\python.exe build\package_dist.py            # 组装 dist-new\AstrBot（这一步会把启动器换成未签名副本）
    4) powershell -NoProfile -ExecutionPolicy Bypass -File build\sign_dist.ps1        # 签 AstrBot.exe + core\AstrBotCore.exe
    4.5) venv\Scripts\python.exe build\make_factory.py         # 落 factory\ 离线自救副本（必须在签名之后：带的是签名后的字节）
    5) tools\innosetup\ISCC.exe /DSourceDir="<绝对路径>\dist-new\AstrBot" build\setup_astrbot.iss
    6) powershell -NoProfile -ExecutionPolicy Bypass -File build\sign_dist.ps1 -SkipApp -ExtraFile installer\AstrBot-Setup-4.29.3.exe
    7) venv\Scripts\python.exe build\make_release_assets.py     # 免安装包 + SHA256SUMS（必须在签完安装包之后）
    8) venv\Scripts\python.exe build\audit_package.py --zip installer\AstrBot-4.29.3-desktop-portable.zip
       目录扫描改成显式 opt-in 了（--path）：正在用的交付目录里本来就有 data\（使用者自己的口令哈希、
       聊天库、他自己装的 NapCat），把它跟"交付物混进了使用者数据"算进同一个退出码，等于每次出包
       都得先把 data 挪走。打包前的目录体检要跑就显式给：... audit_package.py --path dist-new\AstrBot
       安装程序那条由 test\verify_installer.py 管（它断言 data\ 既没被装进去、也没被删掉）。
    9) venv\Scripts\python.exe test\run_all.py                  # 十四步全套，对着签名后的成品跑
      10) venv\Scripts\python.exe test\verify_installer.py      # 装一遍卸一遍（19 项）

只改界面不用重打核心：编译完（第 2 步）直接走第 3、4 步即可。
但第 3 步的闸门会拦住"组装目录里已经有 data\"的情况——那是上一轮测试跑出来的聊天记录，不是构建输入。
这时候不要加 --allow-data（2026-10-29 就是这么把一份 data\ 删掉的），只把新编出来的启动器覆盖过去就够了：
    copy build\launcher\AstrBot.exe dist-new\AstrBot\AstrBot.exe
    copy build\README.txt        dist-new\AstrBot\README.txt        # 包内说明也是构建输入，别留下上一轮的旧数字
然后照常走第 4 步（签名会把两个 exe 一起签）。
成品就是 dist-new\AstrBot 整个目录（或它的压缩包 / 安装程序）；使用者自己的 data\ 永远不要拿去当构建目标。

首次创建签名证书（以后复用同一张）：
    powershell -NoProfile -ExecutionPolicy Bypass -File build\make_signing_cert.ps1
签完当场验一次篡改检测：
    powershell -NoProfile -ExecutionPolicy Bypass -File build\signature_proof.ps1 -Target dist-new\AstrBot\AstrBot.exe
    venv\Scripts\python.exe test\verify_signature.py


十一、签名与来源校验（以及它不能替您做什么）
-----------------------------------------
启动器、核心、安装程序三个文件都用同一张**自签**代码签名证书签了，并带 RFC3161 时间戳：
  · 私钥只在这台构建机的当前用户证书库里，不入库、不随包走；包里放的是公钥 astrbot-signing.cer
  · 它能证明的：成品里的 AstrBot.exe / core\AstrBotCore.exe / AstrBot-Setup-*.exe 确实是这次构建出来的，
    之后**改一个字节都会被报 HashMismatch**
    （build\signature_proof.ps1 就是当场把包体改一字节验的，它临时把公钥装进本机信任库、验完立刻撤掉）
  · 时间戳的作用：证书 3 年后过期，签名照样有效、照样能核对
  · 它**不能**替您做的：自签证书没有权威机构背书，Windows SmartScreen 和杀毒软件**仍会弹警告**——
    这一点不会因为签名而改变，别把它当成免警告的手段。想彻底免警告，只有两条路：
      - 买 OV/EV 代码签名证书（DigiCert / GlobalSign / Sectigo，一年几百到几千美元，EV 要硬件 token）
      - Azure Trusted Signing（约 $9.99/月，需要 Azure 订阅 + 组织验证），签法换成：
          azure codingsignature sign-account ...  或 signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /csp "..." ...
    换证书后 build\sign_dist.ps1 只要把 -App 之外的证书来源改掉即可，其余流程不变。
  · 第三方自带的文件不签：ffmpeg.exe、runtime\ 里的 python.exe 和它的 DLL 保持上游原样
    （把我们的名字盖到别人编的二进制上等于冒充作者），它们的完整性由打包时的版本固定保证。

拿到包的人要不要「信任」这张自签证书？不必，也不建议随便装进受信任根——那台机器从此会信任
用这把私钥签的任意程序。只想核对的话：右键 exe → 属性 → 数字签名，能看到签名者和时间戳即可。

界面是 Win11 风格：深色卡片 + 圆角（窗口/卡片/按钮/输入框都是 8~12px 圆角，状态徽标是胶囊），
悬停有状态变化，标题栏走 DWM 深色 + 圆角 + 尝试 Mica 背景；关闭按钮缩到托盘。
