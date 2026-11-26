# 跨设备同步

## 结构

**代码公开，词库私有。**

```
socamalo/ms-ime-dict        公开  代码、文档、工具
socamalo/ms-ime-dict-data   私有  只有 changes/<id>.<rev>.json
```

私有仓库里每个变更一个文件：

```json
{"id":"a1b2c3d4e5f6a7b8","rev":1,"op":"upsert","text":"闪狂魔药","code":"skmy","rank":2,"enabled":true,"source":"selection","ts":1764157200,"dev":"3f9a1c2b7d4e"}
```

- `id` 16 位随机串，跨设备稳定
- `rev` 每次修改递增，合并时同 `id` 取 rev 大者
- `op=delete` 是 tombstone
- `dev` 是随机设备标识，只用于排查，不含任何个人信息

**不记录**：来源应用、窗口标题、剪贴板历史、选中位置、时间以外的上下文。

## 为什么不用 sqlite 直接同步

sqlite 是二进制页结构，每次写入整文件变化，两台机器同时提交会产生无法合并的冲突，
而且同一文件被两端同时打开有损坏风险。

改成「每次变更一个不可变文件」后：

- 两台机器同时加词 = 各自新增不同文件，git 永远不会报 conflict
- 冲突只可能发生在同一个 `id` 的两次修改上，而那是同一个词的改名，取 rev 大者即可
- 历史可读、可审计、可回滚

## 首次配置

```powershell
# 1. 建私有仓库（GitHub 上手动建空仓库）
# 2. 配置
$settings = "$env:APPDATA\MsImeDict\settings.json"
# 写入 repoPath 与 remoteUrl 后，托盘菜单「同步到私有仓库」
```

## 冲突处理

拉取时如果 `git pull --rebase` 失败，程序会 `rebase --abort` 并报告，
不做自动强推。这种情况只会在手工改过私有仓库时出现。

## 隐私开关

- 默认只同步「你主动点加入的词」
- 可选：提交前用 age 加密整份变更文件，代价是 diff 不可读、合并需先解密
