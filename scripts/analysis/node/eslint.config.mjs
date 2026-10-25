import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const require = createRequire(new URL('./package.json', import.meta.url));
const ts = require('typescript-eslint');
const solid = require('eslint-plugin-solid');
if (!solid.configs['flat/typescript']) throw new Error('Pinned Solid plugin lacks flat/typescript configuration');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const files = ['src/UI/Frontend/src/**/*.{ts,tsx}'];
export default [
  { ignores: ['**/node_modules/**', '**/dist/**', '**/third_party/**', '**/generated/**'] },
  ...ts.configs.strictTypeChecked.map(config => ({ ...config, files })),
  { ...solid.configs['flat/typescript'], files },
  { files, rules: { '@typescript-eslint/no-floating-promises': ['error', { ignoreVoid: false }], '@typescript-eslint/no-unsafe-type-assertion': 'error' }, languageOptions: { parserOptions: { project: path.join(root, 'src/UI/Frontend/tsconfig.json'), tsconfigRootDir: root } } }
];


