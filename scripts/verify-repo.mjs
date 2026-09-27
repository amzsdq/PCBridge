import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const required = [
  'README.md',
  '.gitignore',
  'THIRD_PARTY_NOTICES.txt',
  'docs/VNEXT_SPEC.md',
  'docs/ARCHITECTURE.md',
  'docs/PERMISSION_MODEL.md',
  'docs/RELAY_RELIABILITY.md',
  'docs/HITL_AND_CONTROL_PLANE.md',
  'docs/BENCHMARKS.md',
  'docs/ROADMAP.md',
  'docs/BASELINE.md',
  'src/current/PCBridgePortable.cs',
  'src/current/ScopedBridge.cs',
  'src/current/DesktopIntegration.cs',
  'src/current/IntegrationTests.cs',
  'src/current/ScopeTests.cs'
];

const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: root })
  .toString('utf8').split('\0').filter(Boolean);

const missing = required.filter(p => !tracked.includes(p));
if (missing.length) throw new Error('missing required files: ' + missing.join(', '));

const forbiddenPath = /(^|\/)(?:node_modules|playwright\/\.auth|IntegrationFixtures|ScopeFixtures|TestArtifacts)(\/|$)|\.(?:exe|dll|dpapi|pfx|p12|key|zip)$/i;
const badPaths = tracked.filter(p => forbiddenPath.test(p));
if (badPaths.length) throw new Error('forbidden tracked artifacts: ' + badPaths.join(', '));

const secretPatterns = [
  { name: 'OpenAI-style secret', re: /\bsk-[A-Za-z0-9_-]{20,}\b/g },
  { name: 'private key', re: /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/g }
];

for (const rel of tracked) {
  const full = path.join(root, rel);
  let stat;
  try { stat = fs.statSync(full); } catch { continue; }
  if (!stat.isFile() || stat.size > 2_000_000) continue;
  let text;
  try { text = fs.readFileSync(full, 'utf8'); } catch { continue; }
  for (const { name, re } of secretPatterns) {
    re.lastIndex = 0;
    if (re.test(text)) throw new Error(`${name} detected in ${rel}`);
  }
}

console.log(`repository contract PASS: ${tracked.length} tracked files`);
