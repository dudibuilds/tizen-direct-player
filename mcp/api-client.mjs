// Signed client for the RU7100 agent API (core/Api). Used by the MCP servers and the CLI scripts.
//
// signature = base64(HMAC-SHA256(secret, METHOD \n PATH \n TIME \n NONCE \n hex(sha256(body))))
// Headers: X-Ru7100-Key, X-Ru7100-Time (unix seconds), X-Ru7100-Nonce, X-Ru7100-Signature.
import { createHash, createHmac, randomBytes } from 'node:crypto';
import { realpathSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

/** True when the module at moduleUrl is the script node was started with. */
export function isMain(moduleUrl) {
  if (!process.argv[1]) return false;
  try { return realpathSync(fileURLToPath(moduleUrl)) === realpathSync(process.argv[1]); } catch { return false; }
}

export const DEFAULT_API_URL = 'http://127.0.0.1:45551';

// play, queue start/skip wait for source startup on the TV (parity G3 allows up to 300 s).
const SLOW_COMMANDS = new Map([
  ['playback.play', 310_000],
  ['queue.start', 310_000],
  ['queue.skip', 310_000],
  ['queue.set', 60_000],
  ['library.new_episodes', 120_000],
  ['library.list', 60_000],
]);
const DEFAULT_TIMEOUT_MS = 20_000;

export class ApiError extends Error {
  constructor(code, message, status) {
    super(message);
    this.code = code;
    this.status = status;
  }
}

/**
 * Reads the API URL and one key from the environment.
 * Key sources, first match wins: <prefix>_KEY_ID + <prefix>_KEY_SECRET, or <prefix>_KEY_FILE holding
 * {"id","secret"} or a host key file {"keys":[...]} (pick by <prefix>_KEY_ID, else the first entry).
 */
export async function loadApiConfig({ env = process.env, prefix = 'RU7100_API' } = {}) {
  const url = env.RU7100_API_URL || DEFAULT_API_URL;
  const id = env[`${prefix}_KEY_ID`];
  const secret = env[`${prefix}_KEY_SECRET`];
  if (id && secret) return { url, keyId: id, secret };
  const file = env[`${prefix}_KEY_FILE`];
  if (!file) return null;
  const parsed = JSON.parse(await readFile(file, 'utf8'));
  const entries = Array.isArray(parsed.keys) ? parsed.keys : [parsed];
  if (!id && entries.length > 1) throw new Error(`${file} holds ${entries.length} keys; set ${prefix}_KEY_ID to pick one.`);
  const entry = id ? entries.find(key => key.id === id) : entries[0];
  if (!entry?.id || !entry?.secret) throw new Error(`No usable key in ${file}`);
  return { url, keyId: entry.id, secret: entry.secret };
}

export function signRequest({ secret, method, path, body = '', time, nonce }) {
  const bodyHash = createHash('sha256').update(Buffer.from(body, 'utf8')).digest('hex');
  return createHmac('sha256', Buffer.from(secret, 'base64'))
    .update([method, path, time, nonce, bodyHash].join('\n'))
    .digest('base64');
}

/**
 * @param {{url: string, keyId: string, secret: string}} config
 * @param {{allowedCommands?: (name: string) => boolean}} options  local guard on top of the key's scopes
 */
export function createApiClient(config, { allowedCommands } = {}) {
  if (!config?.keyId || !config?.secret) throw new Error('The RU7100 API key is not configured.');

  async function send(method, route, body, timeoutMs) {
    const target = new URL(route.replace(/^\//, ''), config.url.endsWith('/') ? config.url : `${config.url}/`);
    const time = Math.floor(Date.now() / 1000).toString();
    const nonce = randomBytes(16).toString('hex');
    const text = body === undefined ? '' : JSON.stringify(body);
    const headers = {
      Connection: 'close',
      'X-Ru7100-Key': config.keyId,
      'X-Ru7100-Time': time,
      'X-Ru7100-Nonce': nonce,
      'X-Ru7100-Signature': signRequest({ secret: config.secret, method, path: target.pathname, body: text, time, nonce }),
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    let response;
    try {
      response = await fetch(target, { method, headers, body: body === undefined ? undefined : text, signal: AbortSignal.timeout(timeoutMs) });
    } catch (error) {
      const code = error.name === 'TimeoutError' ? 'timeout' : 'unreachable';
      throw new ApiError(code, `RU7100 API ${code === 'timeout' ? 'timed out' : 'unreachable'} at ${config.url}: ${error.cause?.message || error.message}`, 0);
    }
    const raw = await response.text();
    let payload;
    try { payload = JSON.parse(raw); } catch { throw new ApiError('bad_response', `RU7100 API returned HTTP ${response.status} without JSON`, response.status); }
    if (payload.ok === true) return payload.result;
    throw new ApiError(payload.error?.code || 'http_error', payload.error?.message || `HTTP ${response.status}`, response.status);
  }

  return {
    config,
    command(name, args = {}, { timeoutMs } = {}) {
      if (allowedCommands && !allowedCommands(name)) throw new ApiError('forbidden', `This client may not call ${name}`, 0);
      return send('POST', '/api', { command: name, args }, timeoutMs ?? SLOW_COMMANDS.get(name) ?? DEFAULT_TIMEOUT_MS);
    },
    status({ timeoutMs = DEFAULT_TIMEOUT_MS } = {}) {
      if (allowedCommands && !allowedCommands('status')) throw new ApiError('forbidden', 'This client may not call status', 0);
      return send('GET', '/api/status', undefined, timeoutMs);
    },
  };
}
