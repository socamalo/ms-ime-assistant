# 跨设备同步

## 结构

**代码公开，词库私有。**

```
socamalo/ms-ime-dict        公开  代码、文档、工具
<私有仓库>                   私有  只有 changes/*.json
```

私有仓库里每个变更一个文件：

```json
{"id":"a1b2c3d4e5f6a7b8","rev":1,"op":"upsert","text":"汉字输入","code":"hzsr","rank":2,"enabled":true,"source":"selection","ts":1764157200,"dev":"3f9a1c2b7d4e"}
```

- `id` 16 位随机串，跨设备稳定
- `rev` 每次修改递增，合并时同 `id` 取 rev 大者
- `op=delete` 是 tombstone
- `dev` 是随机设备标识，只用于排查，不含任何个人信息

**不记录**：来源应用、窗口标题、剪贴板历史、选中位置。

## 为什么是「每次变更一个文件」

sqlite 是二进制页结构，每次写入整文件变化，两台机器同时提交会产生无法合并的冲突，
而且同一文件被两端同时打开有损坏风险。

改成不可变文件后：

- 两台机器同时加词 = 各自新增不同文件，git 永远不会报 conflict
- 冲突只可能发生在同一个 `id` 的两次修改上，取 rev 大者即可
- 历史可读、可审计、可回滚

## 配置

全部写在 `%APPDATA%\MsImeDict\settings.json`：

```json
{
  "SyncEnabled": true,
  "RepoPath": "%LOCALAPPDATA%\\MsImeDict\\sync-repo",
  "RemoteUrl": "file:///C:/Users/<你>/ms-ime-dict-data.git",
  "Branch": "main"
}
```

- `RepoPath` 是本机的工作副本，程序自己会在里面 `git init` / commit / push
- `RemoteUrl` 先填本地裸库地址也能跑通，验证完换成 GitHub 即可

## 本地跑通（不需要联网）

```powershell
# 1. 建一个裸库当作「远端」
git init --bare C:\Users\<你>\ms-ime-dict-data.git
# 裸库默认 HEAD 指向 master，必须改成 main，否则 clone 出来是空的
git --git-dir=C:\Users\<你>\ms-ime-dict-data.git symbolic-ref HEAD refs/heads/main

# 2. 写配置（见上），然后同步
tools\msimdict.cmd sync
```

验证双设备：

```powershell
git clone C:\Users\<你>\ms-ime-dict-data.git $env:TEMP\dev2
# 往 dev2\changes\ 里放一个新的 <id>.1.json，commit & push
tools\msimdict.cmd sync     # 本机应拉回并重放进母库
tools\msimdict.cmd list
```

## 换成 GitHub

1. 建一个**私有**仓库（不要勾选 README，保持空的）
2. 改 `settings.json`：

```json
"RemoteUrl": "https://github.com/<你>/ms-ime-dict-data.git"
```

3. 首次推送需要凭据。推荐先手工执行一次，让 Windows 凭据管理器记住：

```powershell
cd $env:LOCALAPPDATA\MsImeDict\sync-repo
git remote set-url origin https://github.com/<你>/ms-ime-dict-data.git
git push -u origin main
```

成功后托盘菜单「同步到私有仓库」就不再需要交互。

## 冲突处理

拉取时如果 `git pull --rebase` 失败，程序会 `rebase --abort` 并报告，
不做自动强推。这种情况只在手工改过私有仓库时出现。

## 隐私

- 默认只同步「你主动加入的词」
- 需要更强保护时：提交前用 age 加密每个变更文件，代价是 diff 不可读、合并要先解密
