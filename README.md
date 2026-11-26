# ms-ime-dict

把任意地方选中的文字一键加入微软拼音的个人词库，词库以 sqlite 为母库，
通过一个**私有仓库**跨设备同步。

```
选中文本 ──► 取词 ──► 母库(sqlite) ──► 投影 ──► 微软拼音候选框
                      │
                      └─► 变更日志(jsonl) ──► 私有 Git 仓库 ──► 另一台设备
```

## 它做什么

- 在任意应用里选中文字，按 `Ctrl+Alt+D`（或选区旁的浮条）唤起菜单，
  「加入个人词典」后词条立刻进入母库
- 自动生成拼音编码（中文全拼，含多音字与姓氏处理），落到微软拼音的
  「用户自定义短语」候选里，并可指定候选位
- 词条以 sqlite 存本地，同时写入追加式 JSONL 日志，推到私有仓库实现跨设备同步
- 微软拼音那边是**投影产物**，随时可以从母库重建，丢了不心疼

## 安全边界

这个工具会直接读写微软拼音的私有数据文件。设计上做了三件事：

1. **只写一个文件**：`ChsPinyinEUDPv1.lex`（用户自定义短语）。
   自学习词库 `ChsPinyinUDL.dat` 是哈希表结构、哈希函数未知、204 槽已用 199，
   本项目只读不写。
2. **写前备份，写后校验**：每次写入前对目标目录做快照，
   写入后回读校验（魔数、偏移表自洽、逐条长度、编码字符集、候选位范围），
   任一项不过就回滚。
3. **随时可回滚**：`tools/restore-ime.ps1 -Pristine` 一条命令恢复到快照状态。

```powershell
# 备份
pwsh -File tools/backup-ime.ps1 -Label before-start

# 查看所有快照
pwsh -File tools/restore-ime.ps1 -List

# 演练（不写盘）
pwsh -File tools/restore-ime.ps1 -Pristine -DryRun

# 真正回滚：还原快照内文件 + 删除我们创建的文件
pwsh -File tools/restore-ime.ps1 -Pristine
```

快照默认落在 `.local/ime-backups/`，已在 `.gitignore` 中排除。

## 文档

- [docs/ime-format.md](docs/ime-format.md) —— 微软拼音自定义短语文件格式
  （本机实测 + 社区逆向，含解析陷阱与硬限制）

## 状态

| 阶段 | 内容 |
|---|---|
| v0 | 格式写入器 + 校验 + 回滚工具（**已完成**） |
| v1 | 托盘常驻 + 热键取词 + 拼音编码 + sqlite 母库 + 手动部署 |
| v2 | 浮条 / Ctrl+右键 + 自动部署 + 私有仓库同步 |
| v3 | 词条管理界面、导出 Rime / 搜狗 |

## 构建与运行

需要 .NET 8 SDK。本机用的是免安装版（`%LOCALAPPDATA%\Microsoft\dotnet`），
`build.ps1` 会自动找到它。

```powershell
pwsh -File build.ps1 -Test      # 构建四个项目并跑 9 项自检
```

日常使用两个入口：

```powershell
tools\msimdict.cmd list               # 词库管理（命令行）
tools\msimdict.cmd add 闪狂魔药        # 加词并写入输入法
tools\msimdict.cmd dump               # 看输入法里当前有哪些短语
tools\msimdict.cmd verify             # 体检输入法短语文件
tools\msimdict.cmd clean              # 清空（回到没有自定义短语的状态）

tools\msimdict-app.cmd                # 托盘常驻：Ctrl+Alt+D 取词加词
```

托盘菜单：加入选中的文字 / 立即写入输入法 / 同步到私有仓库 / 词库目录 / 设置。

## 许可

MIT
