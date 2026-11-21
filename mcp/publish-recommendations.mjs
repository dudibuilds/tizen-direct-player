#!/usr/bin/env node
// Publish the Mac recommendation snapshot to the TV (recommendations.set). For schedulers and manual runs.
//
//   node mcp/publish-recommendations.mjs [snapshot.json] [--expected-version N] [--dry-run]
//
// Without a file it reads $RU7100_DATA_DIR/recommendations.json (default local-data/) plus the curation file.
// Needs RU7100_API_URL and RU7100_RECOMMENDATIONS_KEY_FILE (or _KEY_ID + _KEY_SECRET). Prints JSON to stdout;
// exits 1 with {"error": {...}} on stderr when the TV rejects the push (the TV keeps its last good snapshot).
import { readFile } from 'node:fs/promises';
import { mergeCuration, publishSnapshot, readMacSnapshot, toSetArgs } from './recommendations-feed.mjs';

const args = process.argv.slice(2);
let file;
let expectedVersion;
let dryRun = false;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--dry-run') dryRun = true;
  else if (args[i] === '--expected-version') expectedVersion = Number(args[++i]);
  else if (!args[i].startsWith('--') && !file) file = args[i];
  else {
    process.stderr.write('usage: publish-recommendations.mjs [snapshot.json] [--expected-version N] [--dry-run]\n');
    process.exit(64);
  }
}

try {
  const snapshot = file ? mergeCuration(JSON.parse(await readFile(file, 'utf8'))) : await readMacSnapshot();
  const result = dryRun ? toSetArgs(snapshot, { expectedVersion }) : await publishSnapshot(snapshot, { expectedVersion });
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
} catch (error) {
  process.stderr.write(`${JSON.stringify({ error: { code: error.code || 'error', message: error.message } })}\n`);
  process.exit(1);
}
