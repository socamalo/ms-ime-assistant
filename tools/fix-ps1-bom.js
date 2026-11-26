// 给仓库内所有 .ps1 补齐 UTF-8 BOM：PowerShell 5.1 按 ANSI 读无 BOM 文件，中文会变乱码
const fs = require('fs');
const path = require('path');

const root = process.cwd();
const touched = [];

function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.name === '.git' || e.name === 'node_modules' || e.name === 'bin' || e.name === 'obj' || e.name === 'dist') continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { walk(p); continue; }
    if (!e.name.toLowerCase().endsWith('.ps1')) continue;
    const buf = fs.readFileSync(p);
    if (buf.length >= 3 && buf[0] === 0xEF && buf[1] === 0xBB && buf[2] === 0xBF) continue;
    fs.writeFileSync(p, Buffer.concat([Buffer.from([0xEF, 0xBB, 0xBF]), buf]));
    touched.push(path.relative(root, p));
  }
}

walk(root);
console.log(touched.length ? 'added BOM: ' + touched.join(', ') : 'all .ps1 already have BOM');
