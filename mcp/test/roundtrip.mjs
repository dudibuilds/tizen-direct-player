// End-to-end: MCP servers (stdio) -> signed HTTP -> host/ (core/Api over fakes). Run with `npm --prefix mcp test`.
// Builds host/ in Release unless RU7100_SKIP_BUILD=1. Uses temp dirs only; never touches local-data/ or artifacts/.
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { copyFile, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer as createNetServer } from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { createApiClient, signRequest } from '../api-client.mjs';

const exec = promisify(execFile);
const mcpDir = fileURLToPath(new URL('..', import.meta.url));
const repo = path.resolve(mcpDir, '..');
const hostDll = path.join(repo, 'host', 'bin', 'Release', 'net10.0', 'ru7100-host.dll');
const cleanup = [];
let passed = 0;

async function step(name, action) {
  try {
    await action();
    passed++;
    console.log(`ok ${passed} - ${name}`);
  } catch (error) {
    console.log(`not ok - ${name}`);
    throw error;
  }
}

function freePort() {
  return new Promise((resolve, reject) => {
    const server = createNetServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

async function startHost(keysFile, port, dataDir) {
  const child = spawn('dotnet', [hostDll, '--keys', keysFile, '--port', String(port), '--data', dataDir], { stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`host did not start:\n${output}`)), 60_000);
    child.stdout.on('data', chunk => {
      output += chunk;
      if (output.includes('ru7100-host listening')) { clearTimeout(timer); resolve(); }
    });
    child.stderr.on('data', chunk => { output += chunk; });
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`host exited ${code}:\n${output}`)); });
  });
  child.removeAllListeners('exit');
  const stop = () => new Promise(resolve => {
    if (child.exitCode !== null || child.signalCode !== null) return resolve();
    child.once('exit', resolve);
    child.kill('SIGTERM');
  });
  cleanup.push(stop);
  return stop;
}

async function connect(script, env) {
  const client = new Client({ name: 'ru7100-roundtrip', version: '1.0.0' });
  await client.connect(new StdioClientTransport({
    command: process.execPath,
    args: [path.join(mcpDir, script)],
    env: { PATH: process.env.PATH, HOME: process.env.HOME, ...env },
    stderr: 'inherit',
  }));
  cleanup.push(() => client.close());
  return client;
}

async function call(client, name, args = {}) {
  const result = await client.callTool({ name, arguments: args });
  const value = JSON.parse(result.content[0].text);
  if (result.isError) {
    const error = new Error(`${name} failed: ${value.code}: ${value.message}`);
    error.payload = value;
    throw error;
  }
  return value;
}

async function callError(client, name, args = {}) {
  const result = await client.callTool({ name, arguments: args });
  assert.equal(result.isError, true, `${name} should fail`);
  return result.structuredContent;
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

try {
  if (process.env.RU7100_SKIP_BUILD !== '1') {
    await exec('dotnet', ['build', path.join(repo, 'host', 'Ru7100.Host.csproj'), '-c', 'Release', '-nologo', '-v', 'q'], { cwd: repo, maxBuffer: 10 * 1024 * 1024 });
  }

  const work = await mkdtemp(path.join(os.tmpdir(), 'ru7100-roundtrip-'));
  cleanup.push(() => rm(work, { recursive: true, force: true }));
  const secret = () => randomBytes(32).toString('base64');
  const keys = {
    admin: { id: 'admin', secret: secret(), scopes: ['admin'] },
    agent: { id: 'agent', secret: secret(), scopes: ['playback', 'library'] },
    player: { id: 'player', secret: secret(), scopes: ['playback'] },
    recs: { id: 'recs', secret: secret(), scopes: ['recommendations'] },
  };
  const keysFile = path.join(work, 'keys.json');
  await writeFile(keysFile, JSON.stringify({ keys: Object.values(keys) }), { mode: 0o600 });
  const keyFile = name => {
    const file = path.join(work, `${name}.key.json`);
    return writeFile(file, JSON.stringify({ id: keys[name].id, secret: keys[name].secret }), { mode: 0o600 }).then(() => file);
  };
  const tvData = path.join(work, 'tv-data');
  const port = await freePort();
  const url = `http://127.0.0.1:${port}`;
  let stopHost = await startHost(keysFile, port, tvData);
  const direct = name => createApiClient({ url, keyId: keys[name].id, secret: keys[name].secret });

  // ---------------- TV MCP server ----------------
  const tv = await connect('server.mjs', { RU7100_API_URL: url, RU7100_API_KEY_FILE: await keyFile('admin') });
  const agent = await connect('server.mjs', { RU7100_API_URL: url, RU7100_API_KEY_ID: 'agent', RU7100_API_KEY_SECRET: keys.agent.secret });
  const player = await connect('server.mjs', { RU7100_API_URL: url, RU7100_API_KEY_FILE: keysFile, RU7100_API_KEY_ID: 'player' });

  await step('lists the migrated tool names', async () => {
    const names = (await tv.listTools()).tools.map(tool => tool.name);
    for (const name of ['stremio_discover', 'stremio_library', 'stremio_playback', 'stremio_queue', 'app_status', 'player_controls', 'addons', 'account', 'media_status', 'seek_playback'])
      assert.ok(names.includes(name), name);
    const playback = (await tv.listTools()).tools.find(tool => tool.name === 'stremio_playback');
    assert.equal(playback.inputSchema.properties.target, undefined);
    assert.equal(playback.inputSchema.additionalProperties, false);
  });

  await step('stremio_discover search ranks by title', async () => {
    const hits = await call(agent, 'stremio_discover', { action: 'search', query: 'breaking' });
    assert.equal(hits[0].name, 'Breaking Bad');
    assert.equal(hits[0].matchScore, 0.9);
  });

  await step('stremio_discover library shows watch state', async () => {
    const items = await call(agent, 'stremio_discover', { action: 'library', status: 'watching' });
    assert.deepEqual(items.map(item => item.name), ['Breaking Bad']);
    assert.equal(items[0].nextEpisode.id, 'tt0903747:2:1');
  });

  await step('stremio_discover new_episodes returns count and episodes', async () => {
    const result = await call(agent, 'stremio_discover', { action: 'new_episodes' });
    assert.equal(typeof result.count, 'number');
    assert.ok(Array.isArray(result.episodes));
  });

  await step('bare title play needs confirmation, then plays', async () => {
    const proposal = await call(agent, 'stremio_playback', { action: 'play', query: 'Fresh Show' });
    assert.equal(proposal.status, 'confirmation_required');
    const playing = await call(agent, 'stremio_playback', { action: 'play', query: 'Fresh Show', confirmed: true, quality: '1080p' });
    assert.equal(playing.status, 'playing');
    assert.equal(playing.state, 'PLAYING');
    assert.equal(playing.selected.quality, '1080p');
  });

  await step('app_status shows active playback with rising positionMs', async () => {
    const first = await call(agent, 'app_status', { scope: 'stremio_playback' });
    await sleep(1200);
    const second = await call(agent, 'media_status', { scope: 'stremio_playback' });
    assert.equal(first.active, true);
    assert.ok(second.session.positionMs > first.session.positionMs, `${second.session.positionMs} > ${first.session.positionMs}`);
    const all = await call(agent, 'app_status');
    assert.ok(all.api.commands.includes('playback.play'));
  });

  await step('player_controls and transport commands reach the player', async () => {
    assert.equal((await call(agent, 'player_controls', { action: 'select_audio', index: 1 })).session.selectedAudio, 1);
    assert.equal((await call(agent, 'player_controls', { action: 'select_subtitle', index: -1 })).session.selectedSubtitle, -1);
    assert.equal((await call(agent, 'player_controls', { action: 'subtitle_offset', offsetMs: -500 })).session.subtitleOffsetMs, -500);
    assert.equal((await call(agent, 'player_controls', { action: 'stop_after_current' })).session.stopAfterCurrent, true);
    assert.equal((await call(agent, 'stremio_playback', { action: 'pause' })).state, 'PAUSED');
    assert.equal((await call(agent, 'stremio_playback', { action: 'seek', positionSeconds: 120 })).positionMs, 120000);
    assert.equal((await call(agent, 'stremio_playback', { action: 'resume' })).state, 'PLAYING');
    assert.equal((await call(agent, 'stremio_playback', { action: 'enable_binge' })).mode, 'binge');
  });

  await step('legacy probe tool aliases still work', async () => {
    assert.equal((await call(agent, 'set_subtitle_size', { size: 50 })).session.subtitleSize, 50);
    assert.equal((await call(agent, 'seek_playback', { positionMs: 60000 })).action, 'seek');
    assert.equal((await call(agent, 'get_playback_status')).active, true);
  });

  await step('validation errors use the old error payload', async () => {
    const missing = await callError(agent, 'stremio_playback', { action: 'play' });
    assert.deepEqual(missing, { tool: 'stremio_playback', action: 'play', code: 'missing_argument', message: 'query is required' });
    const irrelevant = await callError(agent, 'player_controls', { action: 'subtitle_size', size: 40, index: 1 });
    assert.equal(irrelevant.code, 'irrelevant_argument');
    assert.equal((await callError(agent, 'stremio_queue', { action: 'status', quality: '4k' })).code, 'irrelevant_argument');
  });

  await step('queue start, skip, status, clear', async () => {
    const started = await call(agent, 'stremio_queue', { action: 'start', items: [{ query: 'Breaking Bad S1E1' }, { query: 'Fresh Show', season: 1, episode: 2 }] });
    assert.equal(started.queue.status, 'playing');
    assert.equal(started.queue.total, 2);
    const skipped = await call(agent, 'stremio_queue', { action: 'skip' });
    assert.equal(skipped.queue.currentPosition, 2);
    assert.equal((await call(agent, 'stremio_queue', {})).action, 'status');
    assert.equal((await call(agent, 'stremio_queue', { action: 'clear' })).queue, null);
  });

  await step('library edits', async () => {
    assert.equal((await call(agent, 'stremio_library', { action: 'add', queryOrId: 'Dog Days' })).inLibrary, true);
    const marked = await call(agent, 'stremio_library', { action: 'mark_watched', queryOrId: 'Fresh Show', season: 1, episode: 1 });
    assert.equal(marked.nextEpisode.id, 'tt9999001:1:2');
    const ambiguous = await call(agent, 'stremio_library', { action: 'add', queryOrId: 'space dog' });
    assert.equal(ambiguous.status, 'ambiguous');
  });

  await step('add-ons and account need admin; admin key works', async () => {
    assert.equal((await call(agent, 'addons', { action: 'list' })).addons[0].id, 'com.stremio.torrentio.addon');
    assert.equal((await callError(agent, 'addons', { action: 'install', manifestUrl: 'https://opensubtitles-v3.strem.io/manifest.json' })).code, 'forbidden');
    assert.equal((await callError(player, 'account', { action: 'link_begin' })).code, 'forbidden');
    const installed = await call(tv, 'addons', { action: 'install', manifestUrl: 'https://opensubtitles-v3.strem.io/manifest.json' });
    assert.equal(installed.ok, true);
    assert.equal((await call(tv, 'addons', { action: 'remove', addonId: installed.addon.id })).ok, true);
    const link = await call(tv, 'account', { action: 'link_begin' });
    assert.equal(typeof link.code, 'string');
    assert.equal((await call(tv, 'account', { action: 'link_complete' })).pending, true);
    assert.equal((await call(tv, 'account', { action: 'link_complete' })).authenticated, true);
    assert.equal((await call(agent, 'account', { action: 'status' })).authenticated, true);
  });

  await step('playback-only key cannot edit the library', async () => {
    assert.equal((await callError(player, 'stremio_library', { action: 'remove', queryOrId: 'Dog Days' })).code, 'forbidden');
    assert.equal((await call(player, 'stremio_playback', { action: 'stop' })).state, 'STOPPED');
  });

  // ---------------- Recommendation feed ----------------
  const macData = path.join(work, 'mac-data');
  await import('node:fs/promises').then(fs => fs.mkdir(macData));
  const fixture = path.join(mcpDir, 'test', 'fixtures', 'recommendations.json');
  await copyFile(fixture, path.join(macData, 'recommendations.json'));
  const recsKeyFile = await keyFile('recs');
  const recsEnv = { RU7100_API_URL: url, RU7100_RECOMMENDATIONS_KEY_FILE: recsKeyFile, RU7100_DATA_DIR: macData };
  const recs = await connect('recommendation-server.mjs', recsEnv);

  await step('recommendation server exposes feed tools and no playback tools', async () => {
    const names = (await recs.listTools()).tools.map(tool => tool.name).sort();
    assert.deepEqual(names, ['curate_recommendations', 'get_recommendations', 'get_tv_feedback', 'publish_to_tv', 'refresh_recommendations']);
    assert.equal((await call(recs, 'get_recommendations', { limit: 2 })).items.length, 2);
  });

  await step('publish_to_tv sends rows to the TV', async () => {
    const published = await call(recs, 'publish_to_tv');
    assert.equal(published.version, 1);
    assert.ok(published.rows >= 2);
    const stored = await direct('recs').command('recommendations.get');
    assert.equal(stored.rows[0].id, 'for-you');
    assert.equal(stored.rows[0].items[0].id, 'tt5753856');
    assert.equal(stored.rows[0].items[0].reason, 'Because you like Crime and Mystery');
    assert.ok(stored.rows.some(row => row.title === 'Because you watched Breaking Bad'));
    assert.ok(stored.rows.some(row => row.id === 'new-for-you'));
    assert.equal(stored.source, 'mac/recommendations.py');
  });

  await step('curate publishes automatically with the agent note as reason', async () => {
    const curated = await call(recs, 'curate_recommendations', { items: [{ id: 'tt2442560', note: 'Tommy Shelby awaits' }] });
    assert.equal(curated.curated, 1);
    assert.equal(curated.tvPublish.version, 2);
    const stored = await direct('recs').command('recommendations.get');
    assert.deepEqual(stored.rows[0].items[0], { id: 'tt2442560', type: 'series', name: 'Peaky Blinders', poster: null, reason: 'Tommy Shelby awaits', score: 0.7011 });
  });

  await step('scopes keep playback and recommendations apart', async () => {
    await assert.rejects(direct('player').command('recommendations.set', { rows: [] }), { code: 'forbidden' });
    await assert.rejects(direct('agent').command('recommendations.feedback.list'), { code: 'forbidden' });
    await assert.rejects(direct('recs').command('playback.play', { query: 'Sintel', confirmed: true }), { code: 'forbidden' });
    await assert.rejects(direct('recs').command('library.add', { queryOrId: 'Sintel' }), { code: 'forbidden' });
  });

  await step('an invalid push is rejected and the last good feed stays', async () => {
    await assert.rejects(direct('recs').command('recommendations.set', { rows: [{ id: 'for-you', title: 'For You', items: [{ id: 'nope', type: 'movie', name: 'x' }] }] }), { code: 'invalid_argument' });
    assert.equal((await direct('recs').command('recommendations.get')).version, 2);
  });

  await step('get_tv_feedback pulls events after the ack cursor', async () => {
    const admin = direct('admin');
    await admin.command('dev.record_feedback', { imdbId: 'tt5753856', videoId: 'tt5753856:1:1', kind: 'play_started', positionMs: 0, durationMs: 3_000_000 });
    await admin.command('dev.record_feedback', { imdbId: 'tt5753856', videoId: 'tt5753856:1:1', kind: 'completed', positionMs: 2_950_000, durationMs: 3_000_000 });
    await admin.command('dev.record_feedback', { imdbId: 'tt14452776', kind: 'not_interested' });
    const first = await call(recs, 'get_tv_feedback', { limit: 2, ack: true });
    assert.deepEqual(first.events.map(event => event.kind), ['play_started', 'completed']);
    assert.equal(first.ackedSequence, 2);
    assert.equal(first.hasMore, true);
    const rest = await call(recs, 'get_tv_feedback', {});
    assert.deepEqual(rest.events.map(event => event.imdbId), ['tt14452776']);
    assert.equal(rest.events[0].videoId, null);
  });

  // ---------------- CLIs for the scheduler ----------------
  const cliEnv = { ...process.env, ...recsEnv };
  await step('publish-recommendations CLI publishes a snapshot file', async () => {
    const { stdout } = await exec(process.execPath, [path.join(mcpDir, 'publish-recommendations.mjs'), fixture], { env: cliEnv });
    assert.equal(JSON.parse(stdout).version, 3);
    const dry = await exec(process.execPath, [path.join(mcpDir, 'publish-recommendations.mjs'), '--dry-run'], { env: cliEnv });
    assert.equal(JSON.parse(dry.stdout).rows[0].items[0].id, 'tt2442560');
  });

  await step('publish-recommendations CLI fails cleanly without a key', async () => {
    const { RU7100_RECOMMENDATIONS_KEY_FILE: _unused, ...noKey } = cliEnv;
    await assert.rejects(exec(process.execPath, [path.join(mcpDir, 'publish-recommendations.mjs'), fixture], { env: noKey }), error => {
      assert.equal(error.code, 1);
      assert.match(JSON.parse(error.stderr).error.message, /RU7100_RECOMMENDATIONS_KEY_FILE/);
      return true;
    });
  });

  await step('tv-feedback CLI prints JSON and acks', async () => {
    const page = JSON.parse((await exec(process.execPath, [path.join(mcpDir, 'tv-feedback.mjs'), '--ack'], { env: cliEnv })).stdout);
    assert.equal(page.events.length, 1);
    assert.equal(page.lastSequence, 3);
    const after = JSON.parse((await exec(process.execPath, [path.join(mcpDir, 'tv-feedback.mjs')], { env: cliEnv })).stdout);
    assert.equal(after.events.length, 0);
    assert.equal(after.ackedSequence, 3);
  });

  await step('the TV keeps the feed across a restart', async () => {
    await stopHost();
    stopHost = await startHost(keysFile, port, tvData);
    const stored = await direct('recs').command('recommendations.get');
    assert.equal(stored.version, 3);
    assert.equal((await direct('recs').command('recommendations.feedback.list')).ackedSequence, 3);
  });

  await step('unsigned, replayed and tampered requests are rejected over HTTP', async () => {
    assert.equal((await fetch(`${url}/api/status`, { headers: { 'X-Ru7100-Key': 'admin' } })).status, 401);
    const body = JSON.stringify({ command: 'status', args: {} });
    const time = Math.floor(Date.now() / 1000).toString();
    const nonce = randomBytes(16).toString('hex');
    const headers = {
      'X-Ru7100-Key': 'player', 'X-Ru7100-Time': time, 'X-Ru7100-Nonce': nonce,
      'X-Ru7100-Signature': signRequest({ secret: keys.player.secret, method: 'POST', path: '/api', body, time, nonce }),
    };
    assert.equal((await fetch(`${url}/api`, { method: 'POST', headers, body })).status, 200);
    assert.equal((await fetch(`${url}/api`, { method: 'POST', headers, body })).status, 401);
    const tampered = { ...headers, 'X-Ru7100-Nonce': randomBytes(16).toString('hex') };
    assert.equal((await fetch(`${url}/api`, { method: 'POST', headers: tampered, body })).status, 401);
  });

  console.log(`# ${passed} passed`);
} finally {
  for (const action of cleanup.reverse()) {
    try { await action(); } catch { /* best effort */ }
  }
}
