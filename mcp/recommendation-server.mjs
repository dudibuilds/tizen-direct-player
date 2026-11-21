import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { fetchFeedback, mergeCuration, publishSnapshot, recommendationsClient } from './recommendations-feed.mjs';

const exec = promisify(execFile);
const project = fileURLToPath(new URL('..', import.meta.url));
const data = path.resolve(process.env.RU7100_DATA_DIR || path.join(project, 'local-data'));
const snapshotPath = path.join(data, 'recommendations.json');
const curationPath = path.join(data, 'recommendation-curation.json');
const catalogDir = path.join(data, 'imdb');
const profile = process.env.RU7100_NETFLIX_PROFILE;
const server = new McpServer({ name: 'ru7100-recommendations', version: '0.1.0' });
let refreshing;

function result(value) {
  return { content: [{ type: 'text', text: JSON.stringify(value) }] };
}

async function run(action) {
  try {
    return result(await action());
  } catch (error) {
    const message = error.code === 'ENOENT'
      ? 'No recommendation snapshot yet. Import a Netflix profile and refresh the recommendations.'
      : error.stderr?.trim() || error.message;
    return { isError: true, content: [{ type: 'text', text: message }] };
  }
}

async function getSnapshot() {
  const snapshot = JSON.parse(await readFile(snapshotPath, 'utf8'));
  const curation = await readFile(curationPath, 'utf8').then(JSON.parse).catch(error => {
    if (error.code === 'ENOENT') return [];
    throw error;
  });
  return mergeCuration(snapshot, curation);
}

// TV delivery uses a recommendations-scoped key only; this server never sends playback or library commands.
async function publishToTv({ automatic = false } = {}) {
  const client = await recommendationsClient();
  if (!client) {
    if (automatic) return { skipped: 'Set RU7100_RECOMMENDATIONS_KEY_FILE to publish to the TV.' };
    throw new Error('Set RU7100_RECOMMENDATIONS_KEY_FILE (or RU7100_RECOMMENDATIONS_KEY_ID and _KEY_SECRET) to a recommendations-scoped TV API key.');
  }
  try {
    return await publishSnapshot(await getSnapshot(), { client });
  } catch (error) {
    if (!automatic) throw error;
    return { ok: false, code: error.code || 'error', message: error.message };
  }
}

async function refresh(refreshCatalog) {
  if (!profile) throw new Error('Set RU7100_NETFLIX_PROFILE to the imported profile name.');
  if (refreshing) throw new Error('A refresh is already running.');
  refreshing = (async () => {
    if (refreshCatalog) {
      await exec('bash', [path.join(project, 'scripts', 'refresh-imdb.sh')], {
        cwd: project, env: { ...process.env, RU7100_CATALOG_DIR: catalogDir }, timeout: 300_000,
      });
    }
    const { stdout } = await exec('python3', [path.join(project, 'mac', 'recommendations.py'),
      '--profile', profile, '--database', path.join(data, 'netflix.sqlite'), '--imdb', catalogDir,
      '--output', snapshotPath], { cwd: project, timeout: 120_000 });
    return JSON.parse(stdout);
  })();
  try {
    return await refreshing;
  } finally {
    refreshing = undefined;
  }
}

async function storeCuration(items) {
  const snapshot = await getSnapshot();
  const available = new Set(snapshot.items.map(item => item.id));
  if (items.some(item => !available.has(item.id))) throw new Error('One or more titles are outside the current snapshot.');
  if (new Set(items.map(item => item.id)).size !== items.length) throw new Error('Each title may appear once.');
  await mkdir(data, { recursive: true, mode: 0o700 });
  const work = await mkdtemp(path.join(data, '.curation-'));
  const temporary = path.join(work, 'curation.json');
  try {
    await writeFile(temporary, JSON.stringify(items), { mode: 0o600 });
    await rename(temporary, curationPath);
  } finally {
    await rm(work, { recursive: true, force: true });
  }
  return { curated: items.length };
}

server.registerTool('get_recommendations', {
  description: 'Read the ranked Mac library and the matching summary for the Netflix profile.',
  inputSchema: { limit: z.number().int().min(1).max(60).default(20) },
}, ({ limit }) => run(async () => {
  const snapshot = await getSnapshot();
  return { ...snapshot, items: snapshot.items.slice(0, limit) };
}));

server.registerTool('refresh_recommendations', {
  description: 'Recalculate the Mac library from the imported profile. An optional refresh pulls public IMDb metadata first.',
  inputSchema: { downloadCatalog: z.boolean().default(false) },
}, ({ downloadCatalog }) => run(async () => ({ ...(await refresh(downloadCatalog)), tvPublish: await publishToTv({ automatic: true }) })));

server.registerTool('curate_recommendations', {
  description: 'Put selected titles from the current recommendation snapshot first, with optional agent notes. An empty list clears the agent order.',
  inputSchema: {
    items: z.array(z.object({
      id: z.string().regex(/^tt\d+$/),
      note: z.string().trim().max(280).default(''),
    })).max(60),
  },
}, ({ items }) => run(async () => ({ ...(await storeCuration(items)), tvPublish: await publishToTv({ automatic: true }) })));

server.registerTool('publish_to_tv', {
  description: 'Send the current recommendation snapshot (curated titles first) to the TV as For You rows. The TV keeps its last good feed if the push is rejected. Refresh and curate publish automatically when a key is configured.',
  inputSchema: {},
}, () => run(() => publishToTv()));

server.registerTool('get_tv_feedback', {
  description: 'Read viewing and feedback events recorded on the TV (play_started, progress, completed, abandoned, thumbs_up, thumbs_down, not_interested, added_to_library, removed) after the TV ack cursor. ack=true acknowledges the returned events so later reads return only newer ones.',
  inputSchema: {
    afterSequence: z.number().int().min(0).optional(),
    limit: z.number().int().min(1).max(1000).default(200),
    ack: z.boolean().default(false),
  },
}, ({ afterSequence, limit, ack }) => run(() => fetchFeedback({ afterSequence, limit, ack })));

await server.connect(new StdioServerTransport());
