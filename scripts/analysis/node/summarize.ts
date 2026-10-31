import fs from 'node:fs';
import path from 'node:path';
interface SarifRule { id: string; defaultConfiguration?: { level?: string } }
interface SarifResult {
  ruleIndex?: number;
  ruleId: string;
  level?: string;
  locations?: { physicalLocation?: { artifactLocation?: { uri?: string }; region?: { startLine?: number } } }[];
  message: { text?: string; markdown?: string };
}
interface SarifLog {
  version: string;
  runs: { tool: { driver: { name: string; rules?: SarifRule[] } }; results?: SarifResult[] }[];
}
interface Finding { tool: string; rule: string; severity: string; file: string; line: number; message: string; area?: string }
interface KnipValue { name?: string; line?: number }
interface KnipIssue { file: string; [rule: string]: string | (string | KnipValue)[] }
interface ZigReport { version: string; files: { exitCode: number; file: string; diagnostics: string[] }[] }
const output = path.resolve(process.argv[2]!);
const root = process.cwd();
const findings: Finding[] = [];
const accepted = new Set(JSON.parse(fs.readFileSync(path.join(output,'accepted-results.json'),'utf8').replace(/^\uFEFF/,'')) as string[]);
const sarifFiles = [...accepted].filter(f => f.endsWith('.sarif')).map(f => path.join(output,f));
for (const file of sarifFiles) {
  const sarif = JSON.parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,'')) as SarifLog;
  if (sarif.version !== '2.1.0') throw new Error(`Not SARIF 2.1: ${file}`);
  for (const run of sarif.runs) {
    for (const r of run.results ?? []) {
      const location = r.locations?.[0]?.physicalLocation;
      let uri = location?.artifactLocation?.uri ?? '';
      try { uri = decodeURIComponent(uri); } catch {}
      uri = uri.replace(/^file:\/\/\//,'').replaceAll('\\','/');
      if (/^[A-Za-z]:\//.test(uri)) uri=path.relative(root,uri).replaceAll('\\','/');
      const rule = run.tool.driver.rules?.[r.ruleIndex!] ?? run.tool.driver.rules?.find(x => x.id === r.ruleId);
      findings.push({tool:run.tool.driver.name,rule:r.ruleId,severity:r.level??rule?.defaultConfiguration?.level??'warning',file:uri,line:location?.region?.startLine??0,message:r.message.text??r.message.markdown??''});
    }
  }
}
const knipFile=path.join(output,'knip.json');
if (accepted.has('knip.json') && fs.existsSync(knipFile)) {
  const k=JSON.parse(fs.readFileSync(knipFile,'utf8').replace(/^\uFEFF/,'')) as { issues?: KnipIssue[] };
  for (const issue of k.issues??[]) {
    for (const [rule,values] of Object.entries(issue)) {
      if (!Array.isArray(values)) continue;
      // Knip 6 reports an unused file as an entry in the issue's own `files` list.
      if (rule==='files') { for (const value of values) findings.push({tool:'knip',rule:'unused-file',severity:'warning',file:'src/UI/Frontend/' + (typeof value === 'string' ? issue.file : value.name??issue.file),line:1,message:'Unused file in configured build + tests graph'}); continue; }
      for (const value of values) findings.push({tool:'knip',rule,severity:'warning',file:'src/UI/Frontend/' + issue.file,line:typeof value === 'string' ? 1 : value.line??1,message:typeof value==='string'?value:value.name??JSON.stringify(value)});
    }
  }
}
const coverage: { Zig?: { version: string; files: number; errors: number; scope: string } } = {};
const zigFile = path.join(output, 'zig.json');
if (accepted.has('zig.json') && fs.existsSync(zigFile)) {
  const z = JSON.parse(fs.readFileSync(zigFile, 'utf8').replace(/^\uFEFF/, '')) as ZigReport;
  const errors = z.files.filter(f => f.exitCode !== 0);
  coverage.Zig = { version: z.version, files: z.files.length, errors: errors.length, scope: 'ast-check only' };
  for (const f of errors) findings.push({tool:'Zig',rule:'ast-check',severity:'error',file:f.file,line:1,message:f.diagnostics.join('\n')});
}
const counts=new Map<string, number>();
for(const f of findings) {
  f.area = f.file.startsWith('src/UI/Frontend/')?'frontend':f.file.startsWith('src/Core/Native/')?'native':f.file.startsWith('scripts/')?'scripts':f.file.startsWith('tests/')?'tests':f.file.startsWith('sdk/')?'sdk':'other';
  const key=JSON.stringify([f.tool,f.rule,f.severity,f.area]);
  counts.set(key,(counts.get(key)??0)+1);
}
const rows=[...counts].map(([key,count])=>{const [tool,rule,severity,area]=JSON.parse(key) as [string, string, string, string];return {tool,rule,severity,area,count};}).sort((a,b)=>a.tool.localeCompare(b.tool)||b.count-a.count||a.rule.localeCompare(b.rule));

const summary={total:findings.length,coverage,sarif:sarifFiles.map(f=>path.relative(root,f).replaceAll('\\','/')),counts:rows};
const markdown=['| Tool | Rule | Severity | Area | Count |','|---|---|---|---|---:|',...rows.map(r=>`| ${r.tool} | ${r.rule} | ${r.severity} | ${r.area} | ${r.count} |`)].join('\n')+'\n';
if (process.argv.includes('--stdout')) {
  process.stdout.write(JSON.stringify({summary,findings,markdown}));
} else {
  fs.writeFileSync(path.join(output,'findings.json'),JSON.stringify(findings,null,2));
  fs.writeFileSync(path.join(output,'summary.json'),JSON.stringify(summary,null,2));
  fs.writeFileSync(path.join(output,'summary.md'),markdown);
  console.log(JSON.stringify({total:findings.length,sarifFiles:sarifFiles.length}));
}
