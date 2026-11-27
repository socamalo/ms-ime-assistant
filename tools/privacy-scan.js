// 隐私扫描：确保公开材料里没有用户词库中的真实词条与身份信息。
//
// 用法：node tools/privacy-scan.js
// 退出码 1 表示发现命中，此时不应发版。
//
// 词表取自本机母库，不写进仓库。

const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const home = process.env.MSIMDICT_HOME || path.join(process.env.LOCALAPPDATA || '', 'MsImeDict');
const dbPath = path.join(home, 'phrases.db');

const PUBLIC_FILES = [
  'README.md',
  'CHANGELOG.md',
  'AGENTS.md',
  'LICENSE',
  'Directory.Build.props',
  'docs/ime-format.md',
  'docs/sync.md',
  'docs/design.md',
  'src/MsImeDict.Core/PhraseStore.cs',
  'src/MsImeDict.Core/PinyinEncoder.cs',
  'src/MsImeDict.Core/Mschxudp.cs',
  'src/MsImeDict.Core/ImeDeployer.cs',
  'src/MsImeDict.Cli/Program.cs',
  'src/MsImeDict.App/Program.cs',
  'src/MsImeDict.App/PhraseManagerForm.cs',
  'src/MsImeDict.App/AddPhraseForm.cs',
  'tests/MsImeDict.Tests/Program.cs',
];

// 身份信息，任何情况都不允许出现
const IDENTITY = ['丁燚', '10098961', 'zte.com.cn', '中兴'];

function loadWords() {
  if (!fs.existsSync(dbPath)) {
    console.log(`跳过词条扫描：母库不存在（${dbPath}）`);
    return [];
  }

  let DatabaseSync;
  try {
    ({ DatabaseSync } = require('node:sqlite'));
  } catch {
    console.log('跳过词条扫描：当前 Node 不支持 node:sqlite');
    return [];
  }

  const db = new DatabaseSync(dbPath);
  const rows = db.prepare('select text, code, abbr from phrases where deleted=0').all();
  db.close();

  const words = new Set();
  for (const r of rows) {
    // 中文词条一律检查；ASCII 编码/简拼只在 3 字符以上才查，
    // 两位编码（如 bj）会和日常英文、地名简写正常撞车，查了全是噪音。
    if (r.text && r.text.length >= 2) words.add(r.text);
    for (const v of [r.code, r.abbr]) {
      if (v && v.length >= 3) words.add(v);
    }
  }
  return [...words];
}

const words = loadWords();

// 中文词条按子串匹配；ASCII 编码按词边界匹配，避免 "bj" 命中 "subject"
const isAscii = (s) => /^[\x20-\x7e]+$/.test(s);

function scan(file, text, pattern, label) {
  const hits = [];
  const lines = text.split(/\r?\n/);

  if (isAscii(pattern)) {
    const re = new RegExp(`\\b${pattern.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`);
    lines.forEach((line, i) => {
      if (re.test(line)) hits.push({ file, line: i + 1, label, text: line.trim().slice(0, 80) });
    });
  } else {
    lines.forEach((line, i) => {
      if (line.includes(pattern)) hits.push({ file, line: i + 1, label, text: line.trim().slice(0, 80) });
    });
  }
  return hits;
}

const hits = [];
for (const rel of PUBLIC_FILES) {
  const p = path.join(root, rel);
  if (!fs.existsSync(p)) continue;

  const text = fs.readFileSync(p, 'utf8');

  // 身份信息：AGENTS.md 里那一段是规则本身，允许出现
  if (rel !== 'AGENTS.md') {
    for (const t of IDENTITY) hits.push(...scan(rel, text, t, t));
  }

  for (const w of words) hits.push(...scan(rel, text, w, w));
}

if (hits.length === 0) {
  console.log(`隐私扫描通过（检查 ${PUBLIC_FILES.length} 个公开文件，${words.length} 个词条）`);
  process.exit(0);
}

console.error('隐私扫描未通过，以下内容不允许出现在公开文件里：');
for (const h of hits) console.error(`  ${h.file}:${h.line}  含「${h.label}」  ${h.text}`);
process.exit(1);
