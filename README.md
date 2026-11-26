# 微软输入法助手

给微软拼音装一个**可自己维护、能跨设备同步**的个人词库。

选中文字 → 一键入词 → 立刻能打出来；词库存在你自己的私有 Git 仓库里，换电脑一句话拉回来。

<p align="center">
  <img src="docs/images/quickbar.png" alt="划词浮条" width="420">
</p>

---

## 这个工具解决什么问题

用微软拼音的人都遇到过这几件事。

**1. 自己造的词，下次还得重新选一遍。**
微软拼音确实会"自学习"，但它学的是你在候选里选过的词，而且这个库有上限、会淘汰。
专业术语、人名、公司名、产品代号、内部缩写——每次都要一个字一个字挑，挑完过阵子又被挤掉。

**2. 官方没有批量导入的接口。**
设置里只有一个"导入 .dat"的按钮，没有 API、没有命令行。
想加一百个词，只能一个个手打。

**3. 换电脑就丢。**
微软拼音的个人词库**不随微软账户同步**。重装系统、换机器，几年积累的词库就没了。

**4. 第三方输入法能同步，但你未必能换。**
搜狗、QQ 拼音的同步很成熟，可很多公司环境、很多人的习惯就是系统自带输入法。
这不是喜好问题，是现实约束。

**5. 现成的划词工具会弄坏你的剪贴板。**
常见的做法是"备份剪贴板 → 模拟 Ctrl+C → 读文本 → 还原"。
一旦还原失败，你刚截的图、复制的文件就没了。这个坑我们踩过，所以这个工具**不写剪贴板**。

### 这个工具怎么做

| | 做法 |
|---|---|
| 加词 | 划词浮条一键加入，或 `Ctrl+C` 后按热键 |
| 写入 | 直接生成微软拼音的"用户自定义短语"文件，立刻可用 |
| 存储 | sqlite 为母库，每次变更一个 JSON 文件推到你的**私有**仓库 |
| 安全 | 写入前备份、写入后回读校验、失败自动回滚、一条命令恢复原状 |

---

## 它不做什么

明确边界，避免误会：

- **不碰剪贴板**：不发 `Ctrl+C`、不 `SetText`、不做"备份还原"。剪贴板永远是你的。
- **不碰其他应用**：不发 `WM_GETOBJECT`、不注入进程、不改系统设置。
- **不写微软拼音的自学习词库**（`ChsPinyinUDL.dat`）。那个文件是 204 槽的哈希表，
  哈希函数未知、几乎写满，硬写会把词库搞坏。本工具只写"用户自定义短语"这一个文件。
- **不上传任何上下文**：私有仓库里只有「词 + 编码 + 候选位」，没有来源应用、窗口标题、剪贴板历史。

---

## 快速开始

### 用发布包（推荐）

1. 到 [Releases](../../releases) 下载 `msimdict-app-win-x64.zip`，解压到任意目录
2. 双击 `msimdict-app.exe`，托盘出现"微软输入法助手"
3. 试用：选中一段文字 → 浮条点「加入词典」→ 在任意输入框打它的拼音

自包含发布包，**不需要安装 .NET 运行时**。

### 从源码构建

```powershell
# 需要 .NET 8 SDK
pwsh -File build.ps1 -Test      # 构建四个项目并跑 9 项自检
tools\msimdict-app.cmd          # 启动托盘程序
```

---

## 怎么用

### 两种加词方式

| 方式 | 操作 | 适用 |
|---|---|---|
| 划词 | 拖动选中 → 浮条「加入词典」 | 浏览器、编辑器等能暴露选区的应用 |
| 热键 | `Ctrl+C` 复制 → `Ctrl+Q` | 任何应用，包括自绘界面、终端 |

热键默认 `Ctrl+Q`，在「设置」里可以改。
加完会浮出一条「「xxx」已加入词典」，1.2 秒自动消失。

### 管理词库

托盘右键 →「管理词库…」：查看所有词条、编码、候选位，支持新增、删除、去重。

### 跨设备同步

托盘右键 →「设置…」→ 填一个**空的私有仓库地址** → 「保存并同步」。

首次同步会自动克隆、推送，之后每次加词后台自动同步。
拉回来的词条会自动重放进母库并写入输入法，不用手动操作。

---

## 安全与回滚

这个工具会写一个系统文件：`%APPDATA%\Microsoft\InputMethod\Chs\ChsPinyinEUDPv1.lex`。

三层保护：

1. **写前备份**：每次写入前把原文件复制到 `%LOCALAPPDATA%\MsImeDict\backups\`
2. **写后校验**：回读文件检查魔数、偏移表自洽、逐条长度、编码字符集、候选位范围
3. **失败回滚**：任何一步不过就恢复备份，绝不留下半成品

整体回滚（连输入法数据目录一起）：

```powershell
pwsh -File tools\backup-ime.ps1 -Label before-try     # 先拍快照
pwsh -File tools\restore-ime.ps1 -List                # 看所有快照
pwsh -File tools\restore-ime.ps1 -Pristine -DryRun    # 演练，不写盘
pwsh -File tools\restore-ime.ps1 -Pristine            # 真正还原
```

`-Pristine` 会把数据目录还原到快照状态，并删除本工具创建的文件（包括那个 `.lex`），
也就是回到"从来没加过自定义短语"的干净状态。每个文件都按 sha256 校验。

---

## 工作原理

```
选中文字 / 剪贴板
        │
        ▼
  PinyinEncoder  生成全拼 + 简拼两种编码
        │
        ▼
  PhraseStore    sqlite 母库 + changes/<id>.<rev>.json
        │         （私有仓库只同步这些 JSON）
        ▼
  ImeDeployer    读现有文件 → 合并 → 按编码升序 → 序列化
        │         → 体检 → 备份 → 原子替换 → 落盘再校验
        ▼
  微软拼音候选框
```

技术细节全部写在 [docs/ime-format.md](docs/ime-format.md)：
`mschxudp` 文件结构、记录必须按编码升序、UTF-16 终止符的解析陷阱、各项硬限制。

同步协议见 [docs/sync.md](docs/sync.md)：为什么不用 sqlite 直接同步、
为什么每次变更一个文件、如何切到 GitHub。

---

## 已知限制

| 限制 | 说明 |
|---|---|
| 词面 ≤ 64 字符 | 微软拼音的硬限制。更长的内容只进母库，不写输入法 |
| 编码 ≤ 32 字符 | 同上。超长的全拼会降级成简拼 |
| 候选位 1–9 | 同一编码最多占 9 个坑 |
| 多音字取首选读音 | 次常用读音会生成没人打的编码，需要别的读音时手工指定 |
| 生效需要几秒 | 输入法自己刷新短语表，通常立刻生效，个别情况要等一会 |

---

## 给 AI Agent 的安装指引

> 这一节是写给自动化 agent 的。人类用户看上面的「快速开始」就够了。
> 目标：装完、配好、验证通过，全程不需要人类点按钮。

### 0. 前提检查（不满足就别继续）

```powershell
# Windows 10 及以上
if (-not ([Environment]::OSVersion.Version.Major -ge 10)) { throw '需要 Windows 10/11' }

# 微软拼音必须已安装——本工具写的是它专属的文件格式，别的输入法不认
$hasPinyin = (Get-WinUserLanguageList | Where-Object { $_.LanguageTag -eq 'zh-Hans-CN' }).Count -gt 0
if (-not $hasPinyin) { throw '没有中文(简体)语言，先安装微软拼音再运行本工具' }
```

### 1. 安装

```powershell
$dir = "$env:LOCALAPPDATA\Programs\MsImeAssistant"
Expand-Archive -Path .\msimdict-app-win-x64.zip -DestinationPath $dir -Force

# 先备份，再启动。顺序不能反。
pwsh -File "$dir\tools\backup-ime.ps1" -Label before-first-run

Start-Process "$dir\msimdict-app.exe"
```

发布包是自包含的，不需要预装 .NET 运行时。

### 2. 首次配置

**快捷键**：默认 `Ctrl+Q`，一般不用改。
`Ctrl+Q` 是**系统级独占**——注册后任何程序里的 `Ctrl+Q` 都会被本工具接管。
如果目标环境里这个组合冲突严重，改配置前先想清楚代价，可选的替代：`Ctrl+Alt+J`、`Ctrl+Shift+F9`。

**同步仓库**（可选但推荐）：

- 先建一个**空的私有仓库**（不要勾 README / .gitignore）
- 地址填仓库网页地址即可，**带不带 `.git` 都认**：

  ```
  https://github.com/你的用户名/仓库名        ← 推荐，直接复制浏览器地址栏
  https://github.com/你的用户名/仓库名.git
  你的用户名/仓库名                          ← 简写也认
  ```

- 程序会自己 `git clone` 或 `git init`，之后每次加词后台自动推送

配置写在 `%APPDATA%\MsImeDict\settings.json`。agent 可以直接改文件，字段如下：

```json
{
  "Hotkey": "Ctrl+Q",
  "DefaultRank": 2,
  "AutoDeploy": true,
  "AutoStart": true,
  "SyncEnabled": true,
  "RepoPath": "%LOCALAPPDATA%\\MsImeDict\\sync-repo",
  "RemoteUrl": "https://github.com/你的用户名/仓库名",
  "Branch": "main"
}
```

改完必须重启进程才生效：`Stop-Process -Name msimdict-app -Force; Start-Process "$dir\msimdict-app.exe"`。

### 3. 开机自启

**方式一：写注册表（推荐）**

```powershell
$exe = "$env:LOCALAPPDATA\Programs\MsImeAssistant\msimdict-app.exe"
New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
  -Name 'MsImeAssistant' -Value "`"$exe`"" -PropertyType String -Force

# 确认
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name MsImeAssistant
```

**方式二：计划任务（需要失败重试时用）**

```powershell
$action   = New-ScheduledTaskAction -Execute $exe
$trigger  = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'MsImeAssistant' -Action $action -Trigger $trigger -Settings $settings -Force
```

**规则：两种只用一种。** 同时开会启动两个实例，第二个注册热键会失败。

注意启动项里是**绝对路径**，移动了安装目录要重写一次。

### 4. 验证（必须做完再交付）

```powershell
# a) 进程在跑
Get-Process msimdict-app -ErrorAction SilentlyContinue

# b) 日志里三个关键行都在
Get-Content "$env:LOCALAPPDATA\MsImeDict\app.log" -Tail 15 -Encoding UTF8
# 期望看到：
#   keyboard hook started=True
#   hotkey register ok=[Ctrl+Q,...]
#   mouse hook started=True

# c) 写入一条测试词，并验证文件结构
& "$dir\msimdict.exe" add 测试词条 --code csz
& "$dir\msimdict.exe" verify
# 期望：体检结果：全部通过

# d) 确认真的进了输入法文件
& "$dir\msimdict.exe" dump | Select-String csz

# e) 清理测试数据
& "$dir\msimdict.exe" remove <上一步 dump 里的 id>
```

想验证真实输入效果，需要人类配合：在任意输入框打 `csz`，候选里应出现「测试词条」。

### 5. 静默命令（不需要界面）

```powershell
& "$dir\msimdict.exe" add "要加的词" --rank 2   # 加词 + 生成编码 + 写入输入法
& "$dir\msimdict.exe" add "词" --code zdy --rank 1   # 指定编码，跳过自动生成
& "$dir\msimdict.exe" list                      # 母库全部词条（含 id）
& "$dir\msimdict.exe" dump                      # 输入法当前的自定义短语
& "$dir\msimdict.exe" verify                    # 结构体检
& "$dir\msimdict.exe" encode 某个词             # 只看会生成哪些编码
& "$dir\msimdict.exe" sync                      # 同步到私有仓库
& "$dir\msimdict.exe" deploy                    # 把母库重新投影进输入法
& "$dir\msimdict.exe" backup                    # 备份当前短语文件
& "$dir\msimdict.exe" rollback                  # 回滚到最近一次备份
& "$dir\msimdict.exe" clean                     # 删除短语文件（回到初始状态）
```

### 6. 必须知道的坑

1. **写入前一定先备份。** 目标文件是微软拼音的私有二进制格式，
   写坏了会让「添加或编辑自定义短语」设置页卡死。恢复办法就是删掉
   `%APPDATA%\Microsoft\InputMethod\Chs\ChsPinyinEUDPv1.lex`。
2. **不要试图结束 `ChsIME.exe` 或 `ctfmon.exe`。** 系统保护进程，`taskkill`
   会被拒绝访问（实测 100% 失败），别浪费时间。
3. **不要写完就断言失败。** 输入法自己决定何时刷新短语表，通常几秒生效。
   如果没出现，让人类在别的输入框打一个字再试。
4. **`Ctrl+Q` 是独占的。** 注册成功后，其他程序的 `Ctrl+Q` 全部失效。
   这是 `RegisterHotKey` 的机制，不是 bug。
5. **`tools\*.ps1` 是 UTF-8 带 BOM。** 编辑后确认 BOM 还在，
   否则 PowerShell 5.1 按 ANSI 读会乱码报语法错。
6. **`*.cmd` 是 GBK 编码。** `cmd.exe` 按 OEM 代码页读脚本，改成 UTF-8 中文会碎。
7. **不要阻塞 UI 线程。** 本程序挂着鼠标/键盘低级钩子，钩子回调被拖慢会让
   **整个系统的输入卡住**。所有网络、磁盘操作都必须在后台线程。
   （这条是踩过的坑：同步曾在 UI 线程同步执行，导致全系统鼠标卡顿。）

### 7. 故障排查

| 现象 | 先看这里 |
|---|---|
| 按热键没反应 | `app.log` 里有没有 `keyboard hook fired`；`hotkey register ok=[...]` 是否为空 |
| 加词了但打不出来 | `dump` 里有没有该编码；有则等几秒或换个输入框 |
| 提示「剪贴板里没有文字」 | 用户没按 `Ctrl+C`，或复制的是图片/文件 |
| 词库删了但输入法还在 | 跑一次 `deploy`（母库是真相层，会整体重写） |
| 同步失败 | `app.log` 里 `sync failed:` 后面的内容；常见是凭据没缓存 |
| 鼠标偶尔卡顿 | 检查是否有长时间同步在 UI 线程跑（本版本已改为后台） |

日志：`%LOCALAPPDATA%\MsImeDict\app.log`
母库：`%LOCALAPPDATA%\MsImeDict\phrases.db`
备份：`%LOCALAPPDATA%\MsImeDict\backups\`
配置：`%APPDATA%\MsImeDict\settings.json`

### 8. 卸载

```powershell
Stop-Process -Name msimdict-app -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name MsImeAssistant -ErrorAction SilentlyContinue
& "$dir\msimdict.exe" clean          # 删掉写进输入法的短语文件
Remove-Item "$env:LOCALAPPDATA\MsImeDict" -Recurse -Force
Remove-Item $dir -Recurse -Force
```

词库数据在私有仓库里，卸载不会丢。换机器重新填仓库地址就能拉回来。

## 作者

**Daniel Ding**（丁燚）
<flaming.ding@gmail.com>

这是一个中文输入法的问题，所以文档全部用中文写。

## 许可

MIT
