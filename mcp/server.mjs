// MCP server for the RU7100 TV app. Tool names and input schemas follow samsung-stremio-spotify-mcp
// (stremio_discover, stremio_library, stremio_playback, stremio_queue) without media targets: playback always
// runs in the TV app's own player. Each call becomes one signed {command, args} request to the TV's agent API.
//
// Env: RU7100_API_URL (default http://127.0.0.1:45551), RU7100_API_KEY_FILE or RU7100_API_KEY_ID + RU7100_API_KEY_SECRET.
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { CallToolRequestSchema, ListToolsRequestSchema } from '@modelcontextprotocol/sdk/types.js';
import { ApiError, createApiClient, isMain, loadApiConfig } from './api-client.mjs';

const noExtra = { additionalProperties: false };
const action = (values, description) => ({ type: 'string', enum: values, description });
const quality = { type: 'string', enum: ['auto', '4k', '1080p'], default: 'auto' };
const maxSizeGb = { type: 'number', exclusiveMinimum: 0, description: 'Optional lower size limit in GB. All streams are capped at 6.5 GB.' };

export const tools = [
  {
    name: 'app_status',
    description: 'Read the TV app status: playback session (active, positionMs, tracks, mode), queue, Stremio account, and the API command list. Use scope=stremio_playback for the playback snapshot only.',
    annotations: { readOnlyHint: true },
    inputSchema: { type: 'object', properties: { scope: { type: 'string', enum: ['all', 'stremio_playback'], default: 'all' } }, ...noExtra },
  },
  {
    name: 'stremio_discover',
    description: 'Search Stremio, inspect the linked library with per-show watch state, or find newly released unwatched episodes that currently have a playable stream. new_episodes returns at most 20 results, newest first.',
    annotations: { readOnlyHint: true },
    inputSchema: {
      type: 'object',
      properties: {
        action: action(['search', 'library', 'new_episodes'], 'Stremio discovery action.'),
        query: { type: 'string' },
        type: { type: 'string', enum: ['auto', 'series', 'movie'], default: 'auto' },
        limit: { type: 'integer', minimum: 1, maximum: 20, default: 8 },
        status: { type: 'string', enum: ['all', 'watching', 'unwatched', 'caught_up', 'library'], default: 'all' },
        sinceLastCheck: { type: 'boolean', default: false },
        acknowledge: { type: 'boolean', default: false },
      },
      required: ['action'],
      ...noExtra,
    },
  },
  {
    name: 'stremio_library',
    description: 'Mutate one Stremio library item: add, remove, change notifications, or mark a movie or episode watched/unwatched.',
    inputSchema: {
      type: 'object',
      properties: {
        action: action(['add', 'remove', 'set_notifications', 'mark_watched'], 'Stremio library mutation.'),
        queryOrId: { type: 'string' },
        notifications: { type: 'boolean' },
        season: { type: 'integer', minimum: 0 },
        episode: { type: 'integer', minimum: 0 },
        watched: { type: 'boolean', default: true },
      },
      required: ['action', 'queryOrId'],
      ...noExtra,
    },
  },
  {
    name: 'stremio_playback',
    description: 'Play Stremio content in the TV app or control the active session. For play: a bare series title returns status=confirmation_required with a resume/next-episode proposal — re-call with confirmed=true to start. Explicit season+episode plays immediately without confirmation. Movies always need confirmed=true. positionSeconds on play overrides resume.',
    inputSchema: {
      type: 'object',
      properties: {
        action: action(['play', 'pause', 'resume', 'stop', 'seek', 'enable_binge', 'disable_binge'], 'Stremio playback action.'),
        query: { type: 'string' },
        season: { type: 'integer', minimum: 0 },
        episode: { type: 'integer', minimum: 0 },
        confirmed: { type: 'boolean', default: false },
        mode: { type: 'string', enum: ['single', 'binge'], default: 'single' },
        quality,
        maxSizeGb,
        positionSeconds: { type: 'number', minimum: 0 },
      },
      required: ['action'],
      ...noExtra,
    },
  },
  {
    name: 'stremio_queue',
    description: 'Create, start, inspect, clear, or skip within a persistent manually ordered cross-show episode queue.',
    inputSchema: {
      type: 'object',
      properties: {
        action: { ...action(['set', 'start', 'status', 'clear', 'skip'], 'Queue action.'), default: 'status' },
        items: {
          type: 'array',
          minItems: 1,
          items: {
            type: 'object',
            properties: {
              query: { type: 'string', description: "Show title or Stremio ID. May include S/E in the text, e.g. 'Breaking Bad S2E5'." },
              season: { type: 'integer', minimum: 0 },
              episode: { type: 'integer', minimum: 0 },
            },
            required: ['query'],
            additionalProperties: false,
          },
          description: 'Omit season and episode to queue the next unwatched (or resumable) episode of that show.',
        },
        quality,
        maxSizeGb,
      },
      ...noExtra,
    },
  },
  {
    name: 'player_controls',
    description: 'Adjust the active TV playback: audio track, subtitle track (index -1 turns subtitles off), subtitle size (24-72), subtitle timing offset in ms (positive delays captions, ±30000), or stop after the current item. Track indexes come from app_status session.audioTracks / session.subtitleTracks. The remote uses the same commands.',
    inputSchema: {
      type: 'object',
      properties: {
        action: action(['select_audio', 'select_subtitle', 'subtitle_size', 'subtitle_offset', 'stop_after_current'], 'Player control.'),
        index: { type: 'integer', minimum: -1, maximum: 255 },
        size: { type: 'integer', minimum: 24, maximum: 72 },
        offsetMs: { type: 'integer', minimum: -30000, maximum: 30000 },
        enabled: { type: 'boolean', default: true },
      },
      required: ['action'],
      ...noExtra,
    },
  },
  {
    name: 'addons',
    description: 'List, install (by manifest URL), or remove Stremio add-ons in the TV app. Install and remove need an admin key.',
    inputSchema: {
      type: 'object',
      properties: {
        action: action(['list', 'install', 'remove'], 'Add-on action.'),
        manifestUrl: { type: 'string', description: 'https://…/manifest.json, for install.' },
        addonId: { type: 'string', description: 'Add-on id from list, for remove.' },
      },
      required: ['action'],
      ...noExtra,
    },
  },
  {
    name: 'account',
    description: 'Stremio account in the TV app: status, device link (link_begin returns a code and link to approve; then call link_complete until authenticated), or sign_out. Linking and sign-out need an admin key.',
    inputSchema: {
      type: 'object',
      properties: {
        action: action(['status', 'link_begin', 'link_complete', 'sign_out'], 'Account action.'),
        code: { type: 'string', description: 'Optional link code for link_complete; defaults to the pending one.' },
      },
      required: ['action'],
      ...noExtra,
    },
  },
  // ---- Aliases kept for agents written against earlier servers ----
  {
    name: 'media_status',
    description: 'Alias of app_status (scope all or stremio_playback) for agents migrating from samsung-stremio-spotify-mcp.',
    annotations: { readOnlyHint: true },
    inputSchema: { type: 'object', properties: { scope: { type: 'string', enum: ['all', 'stremio_playback'], default: 'all' } }, ...noExtra },
  },
  legacy('get_playback_status', 'Alias of app_status scope=stremio_playback.', {}, true),
  legacy('resume_playback', 'Alias of stremio_playback action=resume.', {}),
  legacy('pause_playback', 'Alias of stremio_playback action=pause.', {}),
  legacy('stop_playback', 'Alias of stremio_playback action=stop.', {}),
  legacy('seek_playback', 'Seek to a playback position in milliseconds.', { positionMs: { type: 'integer', minimum: 0, maximum: 2_147_483_647 } }),
  legacy('select_audio_track', 'Select a zero-based audio track.', { index: { type: 'integer', minimum: 0, maximum: 255 } }),
  legacy('select_subtitles', 'Select a zero-based subtitle track, or -1 to turn subtitles off.', { index: { type: 'integer', minimum: -1, maximum: 255 } }),
  legacy('set_subtitle_size', 'Set the subtitle font size for the TV overlay.', { size: { type: 'integer', minimum: 24, maximum: 72 } }),
  legacy('set_subtitle_timing', 'Shift subtitles in milliseconds. A positive offset delays captions.', { offsetMs: { type: 'integer', minimum: -30000, maximum: 30000 } }),
];

function legacy(name, description, properties, readOnly = false) {
  return {
    name,
    description,
    ...(readOnly ? { annotations: { readOnlyHint: true } } : {}),
    inputSchema: { type: 'object', properties, required: Object.keys(properties), ...noExtra },
  };
}

function inputError(code, message) {
  return new ApiError(code, message, 400);
}

function withoutAction(args) {
  const { action: _ignored, ...rest } = args;
  return rest;
}

function pick(args, name) {
  return args[name] === undefined ? {} : { [name]: args[name] };
}

/** Maps one MCP tool call to an API request: {command, args} or {status: true}. The API validates arguments. */
export function toRequest(name, args = {}) {
  const requested = args.action;
  const choose = (map, fallback) => {
    const value = requested ?? fallback;
    if (!Object.hasOwn(map, value)) throw inputError('invalid_argument', `action must be one of: ${Object.keys(map).join(', ')}`);
    return map[value];
  };
  switch (name) {
    case 'app_status':
    case 'media_status':
      return { command: 'status', args: args };
    case 'get_playback_status':
      return { command: 'status', args: { scope: 'stremio_playback' } };
    case 'stremio_discover':
      return { command: choose({ search: 'library.search', library: 'library.list', new_episodes: 'library.new_episodes' }), args: withoutAction(args) };
    case 'stremio_library':
      return { command: choose({ add: 'library.add', remove: 'library.remove', set_notifications: 'library.set_notifications', mark_watched: 'library.mark_watched' }), args: withoutAction(args) };
    case 'stremio_playback':
      return {
        command: choose(Object.fromEntries(['play', 'pause', 'resume', 'stop', 'seek', 'enable_binge', 'disable_binge'].map(a => [a, `playback.${a}`]))),
        args: withoutAction(args),
      };
    case 'stremio_queue':
      return { command: choose(Object.fromEntries(['set', 'start', 'status', 'clear', 'skip'].map(a => [a, `queue.${a}`])), 'status'), args: withoutAction(args) };
    case 'player_controls': {
      const command = choose({
        select_audio: 'playback.select_audio',
        select_subtitle: 'playback.select_subtitle',
        subtitle_size: 'playback.subtitle_size',
        subtitle_offset: 'playback.subtitle_offset',
        stop_after_current: 'playback.stop_after_current',
      });
      const allowed = { select_audio: 'index', select_subtitle: 'index', subtitle_size: 'size', subtitle_offset: 'offsetMs', stop_after_current: 'enabled' }[requested];
      const extra = Object.keys(withoutAction(args)).filter(key => key !== allowed);
      if (extra.length) throw inputError('irrelevant_argument', `Unexpected argument${extra.length === 1 ? '' : 's'} for ${requested}: ${extra.join(', ')}`);
      return { command, args: pick(args, allowed) };
    }
    case 'addons':
      return { command: choose({ list: 'addons.list', install: 'addons.install', remove: 'addons.remove' }), args: withoutAction(args) };
    case 'account':
      return { command: choose({ status: 'account.status', link_begin: 'account.link_begin', link_complete: 'account.link_complete', sign_out: 'account.sign_out' }), args: withoutAction(args) };
    case 'resume_playback': return { command: 'playback.resume', args: {} };
    case 'pause_playback': return { command: 'playback.pause', args: {} };
    case 'stop_playback': return { command: 'playback.stop', args: {} };
    case 'seek_playback': return { command: 'playback.seek', args: { positionSeconds: Number(args.positionMs) / 1000 } };
    case 'select_audio_track': return { command: 'playback.select_audio', args: pick(args, 'index') };
    case 'select_subtitles': return { command: 'playback.select_subtitle', args: pick(args, 'index') };
    case 'set_subtitle_size': return { command: 'playback.subtitle_size', args: pick(args, 'size') };
    case 'set_subtitle_timing': return { command: 'playback.subtitle_offset', args: pick(args, 'offsetMs') };
    default:
      throw inputError('unknown_tool', `Unknown tool: ${name}`);
  }
}

export function jsonResult(value) {
  const result = { content: [{ type: 'text', text: JSON.stringify(value, null, 2) }] };
  if (value && typeof value === 'object' && !Array.isArray(value)) result.structuredContent = value;
  return result;
}

export function errorResult(tool, args, error) {
  const payload = {
    tool,
    action: typeof args?.action === 'string' ? args.action : null,
    code: error?.code || 'internal_error',
    message: error?.message || String(error),
  };
  return { isError: true, content: [{ type: 'text', text: JSON.stringify(payload, null, 2) }], structuredContent: payload };
}

export function createServer(getClient) {
  const server = new Server({ name: 'ru7100-tv', version: '0.2.0' }, { capabilities: { tools: {} } });
  server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools }));
  server.setRequestHandler(CallToolRequestSchema, async request => {
    const { name, arguments: args = {} } = request.params;
    try {
      const { command, args: commandArgs } = toRequest(name, args);
      const client = await getClient();
      return jsonResult(await client.command(command, commandArgs));
    } catch (error) {
      return errorResult(name, args, error);
    }
  });
  return server;
}

let clientPromise;
function defaultClient() {
  clientPromise ??= loadApiConfig().then(config => {
    if (!config) throw new ApiError('not_configured', 'Set RU7100_API_KEY_FILE (or RU7100_API_KEY_ID and RU7100_API_KEY_SECRET) to an agent API key for the TV app.', 0);
    return createApiClient(config);
  });
  clientPromise.catch(() => { clientPromise = undefined; });
  return clientPromise;
}

if (isMain(import.meta.url)) {
  await createServer(defaultClient).connect(new StdioServerTransport());
}
