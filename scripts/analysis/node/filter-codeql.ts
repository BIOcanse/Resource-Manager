import fs from 'node:fs';
interface SarifResult {
  locations?: { physicalLocation?: { artifactLocation?: { uri?: string } } }[];
}
interface SarifLog {
  version: string;
  runs: { results: SarifResult[] }[];
}
const [input, output, language] = process.argv.slice(2);
if (!['javascript','cpp'].includes(language)) throw new Error('Specify javascript or cpp');
const sarif = JSON.parse(fs.readFileSync(input,'utf8').replace(/^\uFEFF/,'')) as SarifLog;
if (sarif.version !== '2.1.0') throw new Error('Expected complete SARIF 2.1.0');
let raw=0, included=0, excluded=0;
const excludedFiles = new Set<string>();
for (const run of sarif.runs) {
  raw += run.results.length;
  run.results = run.results.filter(result => {
    let uri = result.locations?.[0]?.physicalLocation?.artifactLocation?.uri ?? '';
    try { uri = decodeURIComponent(uri); } catch {}
    uri = uri.replaceAll('\\','/');
    const inScope = !uri.includes('/third_party/') && !uri.includes('/NonAdaptedOptimizationLab/') &&
      (language === 'cpp' ? uri.startsWith('src/') || uri.startsWith('tests/Native/') || uri.startsWith('sdk/') : uri.startsWith('src/UI/Frontend/src/'));
    if (inScope) included++; else { excluded++; excludedFiles.add(uri); }
    return inScope;
  });
}
const receipt={language,raw,included,excluded,excludedFiles:[...excludedFiles].sort(),policy:'First-party scope, excluding third_party and private Lab; rule/severity/message/flow unmodified'};
if (output === '-') {
  process.stdout.write(JSON.stringify(sarif));
  process.stderr.write(JSON.stringify(receipt)+'\n');
} else {
  const temporary=output+'.scope.tmp';
  fs.writeFileSync(temporary,JSON.stringify(sarif));
  fs.renameSync(temporary,output);
  fs.writeFileSync(output+'.scope.json',JSON.stringify(receipt,null,2));
  console.log(JSON.stringify({language,raw,included,excluded}));
}

