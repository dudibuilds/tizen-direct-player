#!/usr/bin/env node
// Helper for scripts/tv-dev.sh. Signs requests with the admin key from the agent key file (RU7100_API_KEY_FILE).
//   node scripts/tv-dev-api.mjs wait <timeoutSeconds>        poll GET /api/status until the app answers (0: try once)
//   node scripts/tv-dev-api.mjs command <name> [jsonArgs]    run one API command and print the result
import { createApiClient, loadApiConfig } from '../mcp/api-client.mjs';

const env = { ...process.env, RU7100_API_KEY_ID: process.env.RU7100_API_KEY_ID || 'admin' };
const config = await loadApiConfig({ env });
if (!config) throw new Error('Set RU7100_API_KEY_FILE to the agent key file (artifacts/ru7100-api-keys.json).');
const client = createApiClient(config);
const [mode, ...rest] = process.argv.slice(2);

if (mode === 'wait') {
  const deadline = Date.now() + Number(rest[0] ?? 60) * 1000;
  let last = '';
  for (;;) {
    try {
      const status = await client.status({ timeoutMs: 3000 });
      console.log(`API up: ${status?.api?.commands?.length ?? 0} commands, playback ${status?.playback?.active ? 'active' : 'idle'}`);
      process.exit(0);
    } catch (error) {
      last = error.message;
    }
    if (Date.now() >= deadline) {
      console.error(`The app did not answer: ${last}`);
      process.exit(1);
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
} else if (mode === 'command') {
  const result = await client.command(rest[0], rest[1] ? JSON.parse(rest[1]) : {});
  console.log(JSON.stringify(result, null, 2));
} else {
  console.error('Usage: tv-dev-api.mjs wait <seconds> | command <name> [jsonArgs]');
  process.exit(2);
}
