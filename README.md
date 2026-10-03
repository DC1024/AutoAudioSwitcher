[English](README.en.md) | [日本語](README.ja.md)

# Auto Audio Switcher（中文增强版）

根据窗口所在的显示器，自动切换 Windows 默认音频播放设备。

![Auto Audio Switcher](.github/images/screenshot_en.png)

> 本项目的起点是 [maxkagamine/AutoAudioSwitcher](https://github.com/maxkagamine/AutoAudioSwitcher) 的实现思路，
> 在其基础上重写并大幅扩展。原始版权归 Max Kagamine 所有，本项目同样按
> [Apache License 2.0](LICENSE.txt) 授权发布。

---

## 这个版本多了什么

原版只提供安装包，且路由方式固定为「跟随焦点窗口」。本分支增加了下面这些内容：

| 功能 | 说明 |
| --- | --- |
| **免安装便携版** | 单个 `AutoAudioSwitcher.exe`（自包含，无需装 .NET 运行时），双击即用 |
| **双路由模式** | ① 全局模式：跟随焦点窗口；② 按应用模式：每个应用按**自己的窗口**所在显示器路由 |
| **最小化后仍正确** | 窗口最小化或隐藏到后台时，按该窗口**最后所在**的显示器继续路由 |
| **中文界面** | 简体中文 / 日本語 / English，可在设置里随时切换 |
| **图形化设置** | 托盘右键 →「设置…」，含常规、显示器、更新、关于四个页签 |
| **自动更新** | 启动时检查 GitHub Releases，一键下载并自动替换重启 |

### 两种路由模式的区别

- **全局模式（跟随焦点窗口）**——和原版行为一致。所有应用共用同一个默认播放设备，设备跟着**当前获得焦点
  的窗口**走。用键盘 `Alt+Tab` 或点击切到另一台显示器上的窗口，音频就切过去。这个模式用「焦点窗口」而不是
  「鼠标光标」，所以鼠标划过屏幕边缘不会误触发。
- **按应用模式（跟随各自窗口）**——每个正在播放音频的应用，按**它自己的窗口**所在的显示器决定输出。两个
  播放器可以分别在两块屏上出声，互不干扰。

> **多窗口浏览器注意事项**：Windows 的音频会话是按**进程**分组的，同一个浏览器进程里的多个窗口无法分别路由。
> 如果你需要让同一个浏览器的两个窗口分别在不同显示器出声，请用 `--user-data-dir` 参数另起一个独立进程实例：
>
> ```bat
> "C:\Program Files\Google\Chrome\Application\chrome.exe" --user-data-dir="%TEMP%\chrome-display2" --new-window https://music.youtube.com
> ```
>
> Edge / Chromium 系浏览器同理。

---

## 使用

### 便携版（推荐）

1. 从 [Releases](https://github.com/DC1024/AutoAudioSwitcher/releases) 下载 `AutoAudioSwitcher.exe`。
2. 放到一个固定目录（例如 `D:\Tools\AutoAudioSwitcher\`），双击运行。
3. 托盘出现图标即表示已在后台工作。右键图标可打开菜单。

> 便携版会把配置写在 exe 同目录的 `appsettings.json`，日志写在同目录的 `logs\`。
> 想换目录就整个文件夹一起搬走。

### 安装版

下载 `AutoAudioSwitcher-Setup.exe` 运行即可，会自动处理依赖并注册开机自启。

### 配置

右键托盘图标：

- **设置…** —— 图形化配置窗口
  - *常规*：切换路由模式、启用/停用、开机自启、界面语言、日志级别
  - *显示器*：为每块显示器指定播放设备
  - *更新*：检查更新、查看当前版本、查看更新内容
  - *关于*：版本信息与项目地址
- **退出** —— 结束程序

首次运行会自动把检测到的显示器写进配置，之后在「显示器」页签里给每块屏指定设备即可。

### 开机自启

在「设置 → 常规」里勾选**开机时自动启动**，程序会把自己注册到
`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`。不需要管理员权限。

### 日志

日志在 exe 同目录的 `logs\` 下，按天滚动，保留最近 5 个文件。日志级别在设置里调（默认只记 Error）。

---

## 自动更新

程序启动时（可在设置里关闭）会检查 GitHub Releases。发现新版本时托盘会弹出提示，也可以在
「设置 → 更新」里手动检查。下载后会派发一个临时脚本等待程序退出，替换 exe 并重启。

> 自动更新**只对便携版生效**——只有当运行的是单文件发布（同目录没有 `AutoAudioSwitcher.dll`）时才会启用。
> 安装版请从 Releases 页面手动下载新版安装包。

---

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
# 便携版：自包含单文件
dotnet publish AutoAudioSwitcher -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o dist

# 框架依赖版
dotnet publish AutoAudioSwitcher -o publish
```

指定版本号（会写进 exe 的版本信息，自动更新据此比较）：

```bash
dotnet publish AutoAudioSwitcher -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:Version="1.2.0" -o dist
```

> **WinForms 不支持裁剪**，所以自包含单文件大约 114 MB，这是硬限制。
> 在意体积的话用框架依赖构建（几 MB），但需要机器上装 .NET 10 桌面运行时。

### 换仓库发布

`AutoAudioSwitcher.csproj` 里的 `UpdateRepository` 决定自动更新的检查目标，改这一个属性即可：

```xml
<UpdateRepository>你的用户名/仓库名</UpdateRepository>
```

---

## 更新日志

### v1.1.1

**修复：全局模式间歇性不再跟随焦点窗口**

> **症状**：切到 A 显示器正常，切回 B 无反应；手动在 Windows 声音设置里指定一次设备后又能切一次，之后再次失效。
> 也就是「第一次正常，之后就随缘」。

**根因**：程序原本依赖 Windows 的 `EVENT_OBJECT_LOCATIONCHANGE` 事件来感知窗口跨屏移动，
该事件以 `WINEVENT_OUTOFCONTEXT`（进程外）方式注册。
这类钩子有个不易察觉的特性：**只要系统里任意进程创建或销毁窗口，它的全局状态就会被重置**。
而任务栏、开始菜单、音量面板（`ShellExperienceHost`）、输入法候选框、托盘气泡等
都在不停地创建销毁窗口 —— 于是钩子在程序启动几秒后就**静默失效**了：
注册本身返回成功、不抛异常、也没有任何日志，只是回调不再被调用。

**修复方式**：新增一条**独立的 1 秒轮询兜底**，绕开事件通知、直接查询当前前台窗口所在的显示器。
事件正常时两者互为补充，事件失效时轮询照常工作，不再出现「跟丢」。
由于下游有变化去重，停留在同一显示器时不产生任何多余动作。

**同时改进**（让同类问题不再难以察觉）：

- 显示器设置页：**未指定播放设备的显示器会标红**，并在表格上方显示红字提醒；
  指定后提示立即消失。
- 日志：「该显示器未设置播放设备」由 Information 提升为 **Warning** 级别，
  在默认日志级别下也能看到。
- 启动时及插拔显示器后，会主动检查一遍并列出所有未配置的显示器。

> 排查建议：若怀疑跟随仍有问题，可在「设置 → 常规 → 日志级别」里临时改为 `Debug`
> （看到更细的信息可改 `Verbose`），然后复现一次跨屏切换，日志会明确写出
> `Current monitor is "..."` 与 `Switching to "..."`。

### v1.1.0

首个发布版本：免安装便携版、双路由模式、最小化后仍正确、中文界面、图形化设置、自动更新。

---

## 已知限制

- **Windows 音频会话按进程分组**，同进程的多个窗口无法单独路由（见上文的多窗口浏览器方案）。
- **按应用模式依赖一个未公开的 COM 接口**（`IProcessAudioPolicyConfig`），接口 IID 随 Windows 版本变化，
  已在 Win11 上验证。如果当前系统探测不到该接口，「按应用」选项会自动置灰并给出提示，此时请使用全局模式。
- 自包含单文件体积较大（约 114 MB），因为 WinForms 无法裁剪。

---

## 许可

[Apache License 2.0](LICENSE.txt)。原始版权归 Max Kagamine 所有，本分支的修改同样按 Apache 2.0 发布。
