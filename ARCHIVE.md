# 项目封存说明

**封存日期：** 2026-10-04
**最后发布版本：** v1.1.3
**仓库状态：** 停止维护，已设为 archived（只读）

---

## 为什么封存

一句话：**Windows Core Audio 的 per-app 音频路由只有写、没有可靠的读。**

本项目要做两件事：

| 模式 | 行为 |
| --- | --- |
| A（全局） | 焦点窗口在哪个显示器，默认音频设备就切到哪个显示器 |
| B（按应用） | 每个正在出声的应用，按其**自己窗口**所在显示器单独路由 |

**模式 A 已经跑通。** 它依赖 `GetForegroundWindow()` + WinEvent 钩子。路上踩掉两个坑：

- `WINEVENT_OUTOFCONTEXT` 钩子会被任意进程的窗口创建/销毁**静默重置**，不报错、不抛异常，只是从此不再触发 —— v1.1.1 加 1 秒轮询兜底修掉。
- `MMDevice.Selected = true` 是**即发即忘**，Windows 异步应用设备变更，短路判断会读到未落地的旧状态 —— v1.1.3 改成收敛式对账修掉。

v1.1.1 的修复经过真机日志验证（连续 6 次跨屏切换全部正确）。

**模式 B 撞到了平台墙。** 它依赖 `IAudioPolicyConfig::SetPersistedDefaultAudioEndpoint`，而这个接口的行为是：

- **只写不读。** 返回值 `0` 仅表示「持久化偏好写入成功」，**不代表 Windows 已在当前音频会话上应用它**。设置界面会显示正确，实际却仍走旧设备，重开应用又回默认。
- **想验证就得读回绑定关系**，那需要 `IAudioSessionManager2` / `IAudioSessionControl2` 那一套。本项目里 `ProcessAudioPolicyConfig.GetActiveSessionEndpoints` **只有注释、没有实现**。
- 结果就是**只能盲发指令，无法确认结果**。Windows 不按预期应用时，程序无从得知，只能靠定时重下发碰运气。

这是平台层面的限制，不是本项目能绕过的。继续投入的边际收益很低，因此封存。

---

## 已经发布的东西

| 版本 | 内容 | 状态 |
| --- | --- | --- |
| v1.1.0 | 首个中文增强版：双模式、便携版、中文支持、自动更新、图形化设置页 | 已发布 |
| v1.1.1 | 修复全局模式跨屏不跟随（钩子静默失效 → 加轮询兜底） | 已发布，真机验证通过 |
| v1.1.2 | 修复设置窗口点「取消」后残留未保存改动 | 已发布 |
| v1.1.3 | 修复设备切换异步竞态（收敛式对账，连续两次一致才停手） | 已发布 |

## 已推送但未发版的改动

提交 `4279274` 含两处模式 B 修复，**未经真机验证**：

1. **`PerAppAudioRouter` 把「请求成功」当成「已生效」。** 原代码用 `Dictionary<uint, string>` 记住「我已经路由过这个进程」，于是只会尝试一次。改为记录 `(设备, 会话状态)` 元组，会话状态一变就重新下发。
2. **`WindowPositionTracker` 用窗口标题做 key。** 网易云音乐的窗口标题就是当前歌名，**每换一首歌 key 就变一次**，记忆永远不命中。改为**类名优先 + 进程级兜底**。

设计意图是修掉用户报的「模式 B 实际表现得像模式 A，跨屏时音乐被前台窗口带跑」。但因为平台墙的存在，即使这两处都改对了，**依然没有任何手段能确认它真的生效**。

---

## 已知问题

1. **per-app 路由无法验证** —— 架构级限制，见上。
2. **模式 B 与模式 A 行为难以区分** —— 用户实测现象；`4279274` 针对此修复，未验证。
3. **最小化场景** —— 应用最小化后按「之前的窗口位置」路由，依赖 `WindowPositionTracker` 的记忆；类名键和进程键都失效时不保证正确。

---

## 怎么恢复开发

```bash
git clone https://github.com/DC1024/AutoAudioSwitcher.git
cd AutoAudioSwitcher
```

- 仓库是 archived 状态，**需要先在 GitHub 仓库设置里 Unarchive 才能 push**。
- 构建：`dotnet build AutoAudioSwitcher.slnx`
- CI 与发布：`.github/workflows/build.yml`；Release 说明取自 `.github/release-notes/<tag>.md`。

---

## 上游

基于 [maxkagamine/AutoAudioSwitcher](https://github.com/maxkagamine/AutoAudioSwitcher) 重写扩展。原始版权归 Max Kagamine 所有，按 [Apache License 2.0](LICENSE.txt) 授权发布。
