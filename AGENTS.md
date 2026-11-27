# 给 AI Agent 的项目说明

这个文件是这个仓库的**约束与经验库**。动手改代码之前先读完，它能省掉重复踩坑，
也能避免做出用户明确否掉过的东西。

最后更新：2026-11-27（版本 0.2.1）

---

## 1. 这个项目是什么

把用户选中的文字一键加进**微软拼音的个人词库**，词库以 sqlite 为母库、
通过私有 Git 仓库跨设备同步，每次写入都可回滚。

一句话边界：只写 `ChsPinyinEUDPv1.lex` 这一个系统文件。

---

## 2. 硬约束（用户明确要求过，不要协商）

### 2.1 绝不写剪贴板

- 不发 `Ctrl+C`，不调 `SetText`，不做「备份剪贴板 → 模拟复制 → 还原」。
- 曾经有工具这样做，还原失败把用户刚截的图弄没了。
- 现在只有两条只读取词路径：
  - 划词浮条：UIA 读选区（只读）
  - `Ctrl+Q`：读剪贴板里**已经存在**的文字（只读）
- `Ctrl+Q` 是唯一的热键入口。历史上加过「从剪贴板加词」托盘菜单，被用户否掉了，
  不要再加类似的旁路入口。

### 2.2 不查询、不干预其他应用

- 不用 UIA 做跨进程查询（取词那条只读选区是唯一例外）。
- 不发 `WM_GETOBJECT`，不注入进程，不改系统辅助功能标志。

### 2.3 只写自定义短语文件

- 唯一写入目标：`%APPDATA%\Microsoft\InputMethod\Chs\ChsPinyinEUDPv1.lex`
- 自学习库 `ChsPinyinUDL.dat` 只读，永不写入（204 槽哈希表，哈希函数未知，
  几乎写满，硬写会把词库搞坏）。
- `ChsIME.exe` / `ctfmon.exe` 是保护进程，`taskkill` 一定拒绝访问，别试。

### 2.4 词库是字典：一个词一行

| 字段 | 例子 | 说明 |
|---|---|---|
| 词条 | 天安门 | 显示的词 |
| 编码 | tiananmen | 全拼，打全拼命中 |
| 简拼编码 | tam | 每字首字母，打首字母命中 |

- 唯一键是**词条**，不是「词条 + 编码」。换个编码、带空格、大小写不同都算同一个词。
- 重复添加一律拒绝，并回显已有的编码与简拼。
- 用户看词库时必须是**一个词一行**，不要按编码平铺成多行。

### 2.5 候选位固定 1，编码只两条

- 用户自己加的**词**，候选位就是 1。词频交给输入法动态管理。
- **候选位不是配置项。** 曾经把它做成可配置并跟着设置同步，换一台电脑后新词
  掉到了第 2 位。设置同步里不允许再出现这一项（有自检用例守着）。
- 一个词只存两种编码：全拼 + 首字母简拼。多音字的次读音不参与（会产出
  `sijing…` / `tainao…` 这类没人打的编码）。
- 单字词不生成简拼：一个字母会把那个字母的整段候选表顶掉。

### 2.6 取词来源只作诊断，不上界面

- 两个入口分开记：划词浮条记 `selection`，`Ctrl+Q` 记 `clipboard`。
- `source` 字段只进日志（`added: 词 -> 编码 / 简拼  (source=selection)`），
  **不在词库界面显示**。它不参与去重、不影响任何行为，摆出来只会被误读。
- 历史数据里有过错标（划词被记成 clipboard），已随 bug 修复；界面上看不到它，
  所以不需要为历史数据做迁移。

### 2.7 不要自作主张加功能

用户对「我让你改 A，你顺手加了 B」非常敏感。改什么就只改什么；
认为有必要加的，先说明，等确认。

---

## 3. 部署语义

母库 sqlite 是**唯一真相层**，输入法文件是它的**全量投影**：

- 母库删一条 → 部署后输入法里也没有（`KeepForeignEntries` 默认 `false`，
  开着会导致「删了还在」，实测踩过）。
- 一个编码在输入法里只能挂一条记录，`Mschxudp.Serialize` 会拒绝重复编码。
  所以一个词的简拼撞了别的词的全拼时：**全拼优先**，冲突写进输出提示。
- 部署六步：读现有 → 合并 → 按编码升序 → 序列化到临时文件 → 回读体检 →
  备份 + 原子替换 + 落盘后再校验。任一步失败不落盘，失败可 `rollback`。

### 3.1 跨设备合并的确定性规则

两台设备各自加了同一个词，同步时会出现两条不同 id、同一个词的记录，
而词条唯一索引只允许一条活记录。`ApplyChange` 的规则：

1. 同一个词的竞争记录，保留 `updated_at` 较新的那条
2. 时间相同则比较 id（字符串序），大者胜
3. 输的那条标记 `deleted=1`，不再参与唯一索引

规则只依赖数据本身，两端独立计算得出同样结果，迟到的旧变更翻不了盘。
直接 upsert 会在这种情况下撞唯一索引，整个同步失败（0.2.0 踩过）。

---

## 4. 已知技术事实（都是实测出来的，别再重新验证）

### 4.1 文件格式（详见 `docs/ime-format.md`）

- 魔数 ASCII `mschxudp`，头 0x40 字节，偏移表在 0x40，记录区紧随其后。
- **记录必须按编码升序**，违反后整库失效。
- 编码字符集只有 `a-z` 和 `'`；长度 ≤ 32；词面 ≤ 64 字符；候选位 1–9。
- 编码以 ASCII 结尾时其高字节 `0x00` 会和终止符 `00 00` 连成 `00 00`，
  按长度字段定位解析，别用扫描。
- 写入**立即生效**，不需要切换输入法、不需要重启。

### 4.2 简拼必须显式写

- 输入法**不会**自己把 `tiananmen` 映射到 `tam`。打 `tam` 只有系统词。
- 简拼要单独挂一条编码记录。
- **未决**：裸字母形式的简拼编码（`tam`、`hzsr`）实测命中不到，怀疑输入法匹配的是
  带撇号的音节串（内置短语就是 `s'j`、`r'q` 这种写法）。
  验证方式是同时放两条对照记录（`abc` 与 `a'b'c`），敲 `abc` 看哪条命中。
  结论确定后改 `PinyinEncoder.AbbreviationOf`、补一条自检用例，
  并把这段文字改成确定事实。

### 4.3 剪贴板

`System.Windows.Forms.Clipboard` 走 OLE，别的程序占着剪贴板时会阻塞，
能把取词线程挂死 2 秒（日志里的 `STA thread timeout`）。
用 `ClipboardText.cs` 的 Win32 直读：`OpenClipboard` 拿不到锁立即返回 FALSE，
按 40ms × 12 次重试，全程有上限，失败给出可读原因。

### 4.4 键盘钩子

只处理 `WM_KEYDOWN` 而不处理 `WM_KEYUP`，按下集合永远不会清理。
按过一次 `Q` 之后所有 `Q` 的 keydown 都被当成长按重复忽略，热键从此失效。

### 4.5 线程

低级鼠标/键盘钩子挂在 UI 线程上。任何可能慢的操作（git 联网、磁盘写入）
都必须扔后台线程，否则全系统鼠标卡顿。

### 4.6 编码与终端（中文 Windows）

- `.ps1` 必须 UTF-8 **带 BOM**（PowerShell 5.1 按 ANSI 读）。
- `.cmd` 必须 **GBK**。
- `node -e "...中文..."` 会被 PowerShell 吞字符，含中文的脚本写成文件再跑。
- 落盘外部命令的 stdout 用 `cmd /c "... > out.json"`；PowerShell 的 `>` 是 UTF-16LE。

---

## 5. 工程流程

### 5.1 目录

```
src/MsImeDict.Core     格式编解码、母库、部署、同步（无 UI 依赖）
src/MsImeDict.Cli      13+ 命令行命令
src/MsImeDict.App      WinForms 托盘、划词浮条、热键、设置
tests/MsImeDict.Tests  零依赖自检（18 项）
docs/                  格式、同步协议、设计
tools/                 msimdict.cmd / msimdict-app.cmd 等入口
```

### 5.2 常用命令

```powershell
$dn="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:DOTNET_ROOT=$dn; $env:PATH="$dn;"+$env:PATH

# 自检（改完必跑，全绿再往下）
& "$dn\dotnet.exe" run --project tests\MsImeDict.Tests\MsImeDict.Tests.csproj -c Release --nologo

# 发布
& "$dn\dotnet.exe" publish src\MsImeDict.Cli -c Release -o dist\cli --self-contained false --nologo
& "$dn\dotnet.exe" publish src\MsImeDict.App -c Release -o dist\app --self-contained false --nologo

# 验证真的写进了输入法（不要只看母库）
cmd /c "tools\msimdict.cmd dump"
cmd /c "tools\msimdict.cmd verify"
```

### 5.3 版本号与发版（用户要求「专业地更新版本」）

版本号唯一来源：根目录 `Directory.Build.props` 的 `<Version>`。
代码里读 `AppInfo.Version`，不要在别处硬编码。
语义化版本：主版本=不兼容（词库格式/同步协议），次版本=兼容的新功能，
修订号=兼容的修复。

**发版只有一条命令，不要手工拼步骤：**

```powershell
# 1. 改 Directory.Build.props 的 <Version>
# 2. 在 CHANGELOG.md 顶部加一节「## [x.y.z] - yyyy-mm-dd」
# 3. 先空跑，确认包和说明都对
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -DryRun
# 4. 正式发
powershell -ExecutionPolicy Bypass -File tools\release.ps1
```

`tools/release.ps1` 会依次做：

| 步骤 | 失败就停 |
|---|---|
| 从 `Directory.Build.props` 读版本号 | 读不到就不发 |
| 检查本地 tag 是否已存在 | 同一个版本号只能发一次 |
| 跑 18 项自检 | 不全绿不发布 |
| `dotnet publish` 自包含 win-x64 包 | 发布失败就停 |
| 包内体检：README 无真名、README 版本号、exe 版本号 | 三项任一不符就停 |
| 打 tag `vx.y.z` 并推送 | 失败就停 |
| Release 说明取 `CHANGELOG.md` 对应那一节 | 缺那一节就停 |
| `gh release create` 上传资产 | 失败时 tag 已推，重跑上传即可 |

发完核对三件事：

```powershell
gh release list                       # 新版本在最上面，标记 Latest
gh release view vx.y.z --json assets  # 资产大小与本地 zip 一致
gh release view vx.y.z -q .body       # 说明就是 CHANGELOG 那一节
```

规矩：

- 功能没验证通过时**不打 tag**。打上就等于对外声称可用。
- Release 说明不要另写一份，直接取 CHANGELOG，避免两处口径打架。
- 资产名固定 `msimdict-app-win-x64.zip`，换名字会让旧文档的下载链接失效。
- 打错 tag 的处置：删远端与本地 tag、删 Release，改版本号后重发；
  已经有人下载过的版本号不要重用。

### 5.4 提交

- 每次改动落地就本地 commit，一次逻辑改动一个提交，信息用 conventional commit。
- push 需要用户明确指令。
- 提交身份用个人身份（`Daniel Ding`），**不能出现公司邮箱**。

---

## 6. 隐私与对外口径

### 6.1 用户词库里的词条属于个人信息，一律不得出现在公开材料里

这是硬红线，没有例外。禁止出现的位置包括：

- `CHANGELOG.md` 与 GitHub Release 说明
- `README.md`、`docs/`、`AGENTS.md`
- 代码注释、字符串常量、测试夹具
- 提交信息、Issue、任何对外截图

**禁止拿用户真实词条当例子。** 举例统一用中性词：

| 用途 | 用 |
|---|---|
| 三字词 | 天安门 / `tiananmen` / `tam` |
| 两字词 | 北京 / `beijing` / `bj` |
| 四字词 | 汉字输入 / `hanzishuru` / `hzsr` |
| 自定义短语样例 | 示例词 / `shilici` / `slc` |

词库本身只在私有仓库 `socamalo/ms-ime-dict-data` 与本机 sqlite 里出现。

自检命令（改完公开材料后跑一遍，0 命中才算干净）：

```powershell
node tools\privacy-scan.js
```

扫描器会从本机母库取出全部未删除词条，逐个 grep 公开文件；
`丁燚` / `10098961` / `中兴` / 公司邮箱永远在检查表里。
词表只在本机内存里存在，不会写进仓库。发版脚本已把这步接在自检后面，
不通过就不打包、不打 tag。

两位的 ASCII 编码（如 `bj`）不参与扫描——它们和日常英文正常撞车，查了全是噪音；
中文词条与三位以上的编码一律检查。

### 6.2 身份信息

- 仓库是 **public**：README、代码、提交历史里都不能出现用户真名「丁燚」、
  工号 `10098961`、公司名「中兴」或内部产品名。
- 提交身份用 `Daniel Ding`，**不能出现公司邮箱**。

### 6.3 文档约定

- 文档一律中文——这是中文输入法的问题，读者是中文用户。
- README 不要放会破损的图片。
- CHANGELOG 按 `新增 / 变更 / 修复 / 移除 / 迁移` 分类，只写对外行为的变化，
  不写调试过程、不写「我怎么发现这个 bug」、不用第一人称。

---

## 7. 当前未决事项

| 事项 | 状态 |
|---|---|
| 简拼编码是否必须带撇号（`a'b'c` vs `abc`） | 待实测确认；确认后发下一版 |
| 误建的空仓库 `socamalo/-` | 需要用户 `gh auth refresh -s delete_repo` 后才能删 |

已发布：`v0.1.0`（2026-11-26）、`v0.2.0`、`v0.2.1`、`v0.2.2`（2026-11-27）。

---

## 8. 怎么判断一次改动算完成

1. 自检 18 项全绿
2. `dump` 里能看到预期编码，且与母库一致
3. 输入法里实际打得出来（需要用户配合验证输入行为，本机自动化无法验证按键效果）
4. `CHANGELOG.md` 有对应记录（对外行为有变化时）
5. 本地已提交
