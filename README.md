# 灵动岛 · DynamicIsland

Windows 桌面顶部的「灵动岛」：**真的能透出背后画面的液态玻璃**，显示时间、天气、正在播放的音乐、Windows 未读消息、待办事项和 CPU/内存占用。

不是 WinUI3，用 **WPF + 少量 Win32 互操作**实现，所以能做出任意形状的胶囊、真正的背景模糊和逐帧平滑的形变。

```
┌──────────────────────────────────────────────┐
│  22:51       ▁▃▅  正在播放        ☀ 23°      │   ← 收起：一个小胶囊
└──────────────────────────────────────────────┘
                     ↓ 鼠标移上去
┌──────────────────────────────────────────────┐
│ 22:51  ☀ 23°  ·  CPU 12%  内存 68%  ⏱  ⚙   │
│ 广州市 · 晴  28°/36°                          │
│ ┌──────────────────────────────────────────┐ │
│ │ [封面] 歌名 - 歌手       ⏮ ⏯ ⏭          │ │
│ │        网易云音乐  ━━━━━━━━━○──── 1:02   │ │
│ └──────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────────┐ │
│ │ [QQ] 张三                       刚刚  ✕  │ │
│ │      在吗？晚上一起吃饭                   │ │
│ ├──────────────────────────────────────────┤ │
│ │ 〔消息〕 待办 3          共 9 条未读      │ │
│ └──────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘
```

## 功能

- **液态玻璃**：抓取岛背后的屏幕像素 → CPU 盒子模糊 → 线性放大，是**真的透出背后画面**，不是假的半透明贴图。形状、圆角、描边都是一帧帧算出来的。
- **网易云音乐**：走 Windows 系统媒体控制（SMTC），能拿到歌名 / 歌手 / 封面 / 进度，可播放暂停、上一首下一首，**进度条能按住拖动跳转**。同时支持 QQ 音乐、Spotify、酷狗、酷我、PotPlayer 等任何注册了 SMTC 的播放器。
- **天气**：Open-Meteo（免费、无需 API Key），按出口 IP 自动定位、每小时刷新，**定位不准可以手填城市**。点天气块直接打开天气网页。
- **Windows 未读消息**：直接读 Windows 通知中心，QQ、微信、钉钉等的消息都能拿到**真实标题和正文**。消息多的时候岛的大小不变，滚轮往上看更多。
- **待办**：底部「消息 / 待办」页签切换，点圆圈打勾、点 ✕ 删除，存本地文件，关掉再开还在。
- **倒计时 / 闹钟**：计时中时钟缩小、倒计时放大放在它旁边，配金色进度条；到点整岛闪一下 + 跳两下 + 提示音。
- **CPU / 内存监控**：不依赖性能计数器，按占用变色（绿 / 黄 / 红）。
- **完全自定义**：岛的大小 70%~160% 无级调节、音乐卡片 / 消息卡片 / 待办 / 天气 / 性能各自开关（关掉那一行会真的收掉，岛自动变矮）。
- **拖动**：按住岛身空白处就能拖到屏幕任意位置，位置会记住。
- **本地推送接口**：任何脚本都能往岛上推消息。

## 环境要求

- Windows 10 1809+ / Windows 11
- 编译需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 直接运行打包好的 exe **不需要装运行时**

## 快速开始

```bash
git clone https://github.com/<你的用户名>/DynamicIsland.git
cd DynamicIsland
dotnet build -c Release
dotnet run -c Release
```

打包成单文件 exe（自包含，目标机器无需 .NET）：

```bash
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o publish
```

产物约 66 MB。想要体积小（约 2 MB）可以去掉 `--self-contained true`，但目标机器需要装 .NET 10 桌面运行时。

## 使用

| 操作 | 效果 |
| --- | --- |
| 鼠标移到岛上 | 展开 |
| 鼠标移开 | **立刻**收起 |
| 按住岛身空白处拖动 | 移动岛的位置（记住） |
| 点天气块 | 打开天气网页 |
| 点 ⏱ / ⚙ | 倒计时·闹钟 / 设置 |
| 点消息正文 | 跳到对应内容 |
| 悬停某条消息 | 浮出完整内容 |
| 点消息的 ✕ | 从岛上移除这条 |
| 底部「消息 / 待办」 | 切换两个列表 |
| 滚轮 | 列表往上下翻 |
| 右键岛 | 菜单：倒计时·闹钟 / 停止计时 / 设置 / 刷新天气 / 打开配置文件 / 推送接口说明 / 退出 |
| 拖进度条 | 跳转播放位置 |

## 设置

右键岛 →「设置…」，或点展开后右上角的 ⚙。所有滑块**实时预览**。

配置文件在 `%APPDATA%\DynamicIsland\settings.json`，待办在 `%APPDATA%\DynamicIsland\todos.json`。

| 设置项 | 说明 |
| --- | --- |
| 手动城市 | 填了就优先用它，留空才按 IP 定位 |
| 刷新间隔 | 天气多久刷一次，默认 60 分钟 |
| 距离顶部 | 岛离屏幕顶边多远 |
| 模糊强度 / 通透度 | 玻璃的模糊程度和透明度 |
| 消息停留 | 来新消息后岛主动展开的时长 |
| 背景刷新 | 背景模糊多久重抓一次，0 = 只在展开/收起时抓 |
| 岛的大小 | 70% ~ 160% |
| 显示内容 | 音乐 / 消息待办 / 待办页签 / 天气 / CPU内存 / 胶囊显示CPU / 内存详情 |
| 开机自动启动 | 写注册表 Run 项，不需要管理员权限 |
| 端口 | 本地推送接口的端口，0 = 关闭 |

## 本地推送接口

监听 `127.0.0.1`，外部网络访问不到。**UTF-8 和 GB2312 的请求体都能正确解码**（PowerShell 5.1 默认发本地代码页，专门处理过）。

```powershell
# 推一条消息
Invoke-RestMethod -Uri http://127.0.0.1:7788/message -Method Post `
  -Body '{"source":"QQ","title":"张三","text":"在吗"}'

# 纯文本，会当成正文
Invoke-RestMethod -Uri http://127.0.0.1:7788/message -Method Post -Body "构建完成"

# 探活
Invoke-RestMethod -Uri http://127.0.0.1:7788/ping
```

## 实现要点

几个我觉得值得记下来的地方：

- **真背景模糊**：`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 让抓屏抓不到自己（否则会把自身画面反复模糊，形成视频回授），然后用 GDI 抓屏 + CPU 盒子模糊。模糊烘焙进位图里，所以重绘不需要着色器。
- **动画不掉帧**：展开/收起**不修改任何元素的宽高**（那会每帧触发一次布局），只动画剪裁出来的 `RectangleGeometry` 的 Rect 和圆角 —— 纯渲染路径。动画期间也完全不抓屏（抓一次十几毫秒正好卡在动画里）。
- **背景 1:1 对齐**：岛整体缩放时，底图做反向补偿缩放，保证玻璃里的画面和真实屏幕严格对齐。
- **未读消息**：Windows 把当前通知存在 `wpndatabase.db`（SQLite），连着 `-wal`/`-shm` 一起复制到临时目录再只读查询。在通知中心划掉一条，对应行就没了，所以天然就是「未读」。整个读取过程放在后台线程，不卡 UI。
- **CPU 占用**：用 `GetSystemTimes` 的两次采样差值算，不用性能计数器（那玩意儿在某些机器上会坏掉）。
- **无边框窗口拖动**：自己处理 `MouseLeftButtonDown/Move/Up` + `SetWindowPos`，而不是返回 `HTCAPTION`，这样才能区分「点击控件」和「拖动」。

## 已知限制

老实说清楚，免得你以为是 bug：

- **QQ 消息正文来自 Windows 通知**。QQ 自己不把正文暴露在窗口标题里，所以走通知中心这条路才拿得到内容。如果 QQ 没弹通知，就抓不到。
- **消息的 ✕ 只是从岛上移除**，不会划掉通知中心里那条 —— 那需要应用包身份权限，非打包程序拿不到。
- **拖进度条能不能真跳，取决于播放器有没有实现 SMTC 的跳转接口**。接口接了，但网易云要是不支持，拖完会弹回原位。
- **IP 定位经常不准**。我实测同一个 IP，ip-api 说广州、ipwho.is 说北京、ip.sb 说福州，三家互相打架。所以设置里可以手填城市。
- **单文件版首次启动慢 1~2 秒**（要解压），内存也比普通版高。
- 屏幕上有全屏独占程序时，置顶可能失效。

## 项目结构

```
DynamicIsland/
├─ MainWindow.xaml(.cs)          岛本体：布局、玻璃、动画、交互
├─ SettingsWindow / TimerWindow / InputWindow   三个弹窗
├─ Themes/
│  ├─ IslandStyles.xaml          玻璃配色、文字、图标按钮
│  └─ DialogStyles.xaml          弹窗共用皮肤
├─ Models/IslandModels.cs        消息 / 曲目 / 天气
├─ Services/
│  ├─ NowPlayingService.cs       SMTC 媒体控制（网易云音乐等）
│  ├─ NotificationService.cs     读 Windows 通知数据库
│  ├─ WeatherService.cs          Open-Meteo + 自绘矢量天气图标
│  ├─ QqWatcher.cs               QQ 窗口监听 + Shell 钩子
│  ├─ TimerService.cs            倒计时 / 闹钟
│  ├─ TodoService.cs             待办
│  ├─ SystemMonitorService.cs    CPU / 内存
│  ├─ PushServer.cs              本地推送接口
│  ├─ AppSettings.cs / AutoStart.cs
├─ Interop/
│  ├─ NativeMethods.cs           P/Invoke 声明
│  ├─ ScreenCapture.cs           抓屏 + CPU 盒子模糊
│  ├─ ShellHook.cs               任务栏闪烁通知
│  ├─ WindowEnumerator.cs        窗口枚举
│  └─ Diagnostics.cs             开发期自检
└─ tools/                        开发期脚本（跑测试用）
```

## 开发 / 调试

设了环境变量 `DSH_ISLAND_DIAG=1` 后，程序会把自己的画面渲染成 PNG 存到
`%APPDATA%\DynamicIsland\diag\`，并记录窗口几何信息 —— 不用真的去截图就能检查外观。

```powershell
$env:DSH_ISLAND_DIAG = "1"          # 开启自检
$env:DSH_ISLAND_DIAG_EXIT = "1"     # 抓完自动退出
$env:DSH_ISLAND_DIAG_DELAY = "6000" # 等几毫秒再抓（等天气加载完）
```

## 许可

[MIT](LICENSE)
