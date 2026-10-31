import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
const require = createRequire(new URL('./package.json', import.meta.url));
const expected = (require('./package.json') as { devDependencies: Record<string, string> }).devDependencies;
const versions: Record<string, string> = {};
for (const [name, version] of Object.entries(expected)) {
  const actual = (JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), 'node_modules', name, 'package.json'),'utf8')) as { version: string }).version;
  if (actual !== version) throw new Error(`Version mismatch for ${name}: ${actual}, expected ${version}`);
  versions[name] = actual;
}
const { ESLint } = require('eslint') as typeof import('eslint');
const root = process.cwd();
const output = process.argv[2]!;
fs.writeFileSync(path.join(output, 'node-versions.json'), JSON.stringify({node:process.versions.node,packages:versions},null,2));
if (process.argv.includes('--verify-only')) process.exit(0);
const eslint = new ESLint({ cwd: root, overrideConfigFile: path.join(root, 'scripts/analysis/node/eslint.config.mjs') });
const results = await eslint.lintFiles(['src/UI/Frontend/src/**/*.{ts,tsx}']);
const formatter = await eslint.loadFormatter(require.resolve('@microsoft/eslint-formatter-sarif'));
let sarif = await formatter.format(results);
// A result is shareable only after absolute workstation paths have been removed.
sarif = sarif.split('file:///' + root.replaceAll('\\', '/') + '/').join('').split(root.replaceAll('\\', '/') + '/').join('').split(root + path.sep).join('');
fs.writeFileSync(path.join(output, 'eslint.sarif'), sarif);
fs.writeFileSync(path.join(output, 'eslint.json'), JSON.stringify(results.map(r => ({ file: path.relative(root, r.filePath).replaceAll('\\','/'), messages: r.messages, errorCount: r.errorCount, warningCount: r.warningCount })), null, 2));
console.log(JSON.stringify({ files: results.length, errors: results.reduce((n,r)=>n+r.errorCount,0), warnings: results.reduce((n,r)=>n+r.warningCount,0) }));
if (results.some(r => r.messages.some(m => m.fatal))) process.exitCode = 2;




