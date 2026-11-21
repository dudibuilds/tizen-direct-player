// Recommendation feed delivery to the TV, usable from a scheduler, the CLIs and the MCP server alike.
// Reads the snapshot written by mac/recommendations.py (plus the optional curation file), turns it into
// rows for recommendations.set, and pulls viewing/feedback events back with an ack cursor.
// Only recommendations.* commands are allowed through this module's client.
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createApiClient, loadApiConfig } from './api-client.mjs';

const project = fileURLToPath(new URL('..', import.meta.url));
export const SOURCE = 'mac/recommendations.py';
const LIMITS = { forYou: 50, newForYou: 20, becauseRows: 2, becauseItems: 20, becauseMinimum: 3, reason: 140, title: 80 };

export function dataDir(env = process.env) {
  return path.resolve(env.RU7100_DATA_DIR || path.join(project, 'local-data'));
}

/** The recommendations.py snapshot with curated titles first (each carrying agentNote). */
export async function readMacSnapshot(directory = dataDir()) {
  // Prefer the rule-based feed (python3 -m mac.feed build); fall back to the legacy recommendations.py output.
  const feed = await readFile(path.join(directory, 'recommendation-feed.json'), 'utf8').then(JSON.parse).catch(error => {
    if (error.code === 'ENOENT') return null;
    throw error;
  });
  if (feed && Array.isArray(feed.rows)) return feed;
  const snapshot = JSON.parse(await readFile(path.join(directory, 'recommendations.json'), 'utf8'));
  const curation = await readFile(path.join(directory, 'recommendation-curation.json'), 'utf8').then(JSON.parse).catch(error => {
    if (error.code === 'ENOENT') return [];
    throw error;
  });
  return mergeCuration(snapshot, curation);
}

export function mergeCuration(snapshot, curation = []) {
  const available = new Map((snapshot.items || []).map(item => [item.id, item]));
  const selected = [];
  for (const choice of curation) {
    const item = available.get(choice.id);
    if (!item) continue;
    selected.push({ ...item, agentNote: choice.note });
    available.delete(choice.id);
  }
  return { ...snapshot, items: [...selected, ...available.values()] };
}

function clip(text, limit) {
  const value = String(text ?? '').trim();
  return value.length <= limit ? value : `${value.slice(0, limit - 1).trimEnd()}…`;
}

function reasonFor(item) {
  if (item.agentNote?.trim()) return clip(item.agentNote, LIMITS.reason);
  const genres = (item.matchedGenres || []).filter(Boolean);
  return genres.length ? clip(`Because you like ${genres.join(' and ')}`, LIMITS.reason) : '';
}

function card(item, reason = reasonFor(item)) {
  const value = { id: item.id, type: item.type, name: clip(item.name, 200), reason };
  if (Number.isFinite(item.score)) value.score = item.score;
  if (typeof item.poster === 'string' && /^https?:\/\//.test(item.poster)) value.poster = item.poster;
  return value;
}

function usable(item) {
  return /^tt\d{5,10}$/.test(item?.id || '') && (item.type === 'movie' || item.type === 'series') && String(item.name || '').trim();
}

/**
 * Deterministic conversion of a recommendations.py snapshot into TV rows:
 * "For You" (ranked order, curated first), "New for you" (released this or last year),
 * and up to two "Because you watched X" rows from recentlyWatched by shared genres.
 */
export function buildRows(snapshot, { now = new Date() } = {}) {
  const items = (snapshot.items || []).filter(usable);
  if (!items.length) throw new Error('The recommendation snapshot has no usable items.');
  const rows = [{ id: 'for-you', title: 'For You', items: items.slice(0, LIMITS.forYou).map(item => card(item)) }];

  const recentYear = now.getUTCFullYear() - 1;
  const fresh = items.filter(item => Number(item.year) >= recentYear).slice(0, LIMITS.newForYou);
  if (fresh.length) rows.push({ id: 'new-for-you', title: 'New for you', items: fresh.map(item => card(item)) });

  for (const watched of (snapshot.recentlyWatched || []).filter(entry => entry?.name && entry.genres?.length).slice(0, LIMITS.becauseRows)) {
    const genres = new Set(watched.genres);
    const related = items
      .filter(item => item.id !== watched.id)
      .map((item, rank) => ({ item, rank, shared: (item.genres || []).filter(genre => genres.has(genre)) }))
      .filter(entry => entry.shared.length)
      .sort((a, b) => b.shared.length - a.shared.length || a.rank - b.rank)
      .slice(0, LIMITS.becauseItems);
    if (related.length < LIMITS.becauseMinimum) continue;
    const slug = `because-${String(watched.id || watched.name).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}`.slice(0, 40).replace(/-$/, '');
    if (rows.some(row => row.id === slug)) continue;
    rows.push({
      id: slug,
      title: clip(`Because you watched ${watched.name}`, LIMITS.title),
      items: related.map(({ item, shared }) => card(item, item.agentNote?.trim() ? reasonFor(item) : clip(`Like ${watched.name}: ${shared.slice(0, 2).join(', ')}`, LIMITS.reason))),
    });
  }
  return rows;
}

// TV limits (core/Api/Recommendations.cs RecommendationLimits).
const TV = { rows: 20, itemsPerRow: 50, items: 300, title: 80, name: 200, reason: 140, url: 500 };

/** Rows from the mac.feed snapshot, reduced to the fields and limits the TV accepts. */
export function feedRows(snapshot) {
  const rows = [];
  let total = 0;
  for (const row of snapshot.rows) {
    if (rows.length >= TV.rows || total >= TV.items) break;
    const ids = new Set();
    const items = [];
    for (const item of row.items || []) {
      if (items.length >= TV.itemsPerRow || total + items.length >= TV.items) break;
      if (!/^tt\d+$/.test(item.id || '') || !['movie', 'series'].includes(item.type) || ids.has(item.id)) continue;
      ids.add(item.id);
      const value = { id: item.id, type: item.type, name: clip(item.name || item.id, TV.name), reason: clip(item.reason || '', TV.reason) };
      if (typeof item.poster === 'string' && /^https?:\/\//.test(item.poster) && item.poster.length <= TV.url) value.poster = item.poster;
      if (typeof item.score === 'number' && Number.isFinite(item.score)) value.score = item.score;
      items.push(value);
    }
    if (!items.length) continue;
    total += items.length;
    rows.push({ id: String(row.id).slice(0, 64), title: clip(row.title, TV.title), items });
  }
  if (!rows.length) throw new Error('The recommendation feed has no usable rows.');
  return rows;
}

export function toSetArgs(snapshot, { now, expectedVersion } = {}) {
  const args = { rows: Array.isArray(snapshot.rows) ? feedRows(snapshot) : buildRows(snapshot, { now }), source: SOURCE };
  if (typeof snapshot.generatedAt === 'string' && snapshot.generatedAt.length <= 40) args.generatedAt = snapshot.generatedAt;
  if (expectedVersion !== undefined) args.expectedVersion = expectedVersion;
  return args;
}

/** A client limited to recommendations.* using RU7100_RECOMMENDATIONS_KEY_FILE (or _KEY_ID + _KEY_SECRET). */
export async function recommendationsClient(env = process.env) {
  const config = await loadApiConfig({ env, prefix: 'RU7100_RECOMMENDATIONS' });
  if (!config) return null;
  return createApiClient(config, { allowedCommands: name => name.startsWith('recommendations.') });
}

async function requireClient(client) {
  const resolved = client ?? await recommendationsClient();
  if (!resolved) throw new Error('Set RU7100_RECOMMENDATIONS_KEY_FILE (or RU7100_RECOMMENDATIONS_KEY_ID and _KEY_SECRET) to a recommendations-scoped TV API key.');
  return resolved;
}

/** Sends the snapshot to the TV. Returns {ok, version, publishedAt, rows, items}. */
export async function publishSnapshot(snapshot, { client, now, expectedVersion } = {}) {
  return (await requireClient(client)).command('recommendations.set', toSetArgs(snapshot, { now, expectedVersion }));
}

/**
 * Reads events after the TV's ack cursor (or afterSequence). With ack=true, acknowledges what was returned,
 * so the next call yields only newer events. Returns the TV's FeedbackPage plus ackedSequence after the ack.
 */
export async function fetchFeedback({ client, afterSequence, limit, ack = false } = {}) {
  const api = await requireClient(client);
  const args = {};
  if (afterSequence !== undefined) args.afterSequence = afterSequence;
  if (limit !== undefined) args.limit = limit;
  const page = await api.command('recommendations.feedback.list', args);
  if (ack && page.events.length) {
    const acked = await api.command('recommendations.feedback.ack', { sequence: page.lastSequence });
    return { ...page, ackedSequence: acked.ackedSequence };
  }
  return page;
}

export async function ackFeedback(sequence, { client } = {}) {
  return (await requireClient(client)).command('recommendations.feedback.ack', { sequence });
}
