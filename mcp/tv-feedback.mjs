#!/usr/bin/env node
// Pull viewing and feedback events recorded on the TV (recommendations.feedback.list) as JSON on stdout.
//
//   node mcp/tv-feedback.mjs [--after N] [--limit N] [--ack]     read events after the TV's ack cursor (or N)
//   node mcp/tv-feedback.mjs --ack-through N                     only move the TV's ack cursor to N
//
// --ack acknowledges the returned events after they are written to stdout, so the next run yields only newer
// ones. A scheduler that must not lose events should store the output first, then run --ack-through lastSequence.
// Needs RU7100_API_URL and RU7100_RECOMMENDATIONS_KEY_FILE (or _KEY_ID + _KEY_SECRET).
import { ackFeedback, fetchFeedback, recommendationsClient } from './recommendations-feed.mjs';

const args = process.argv.slice(2);
const options = {};
for (let i = 0; i < args.length; i++) {
  const flag = args[i];
  if (flag === '--ack') options.ack = true;
  else if (flag === '--after') options.afterSequence = Number(args[++i]);
  else if (flag === '--limit') options.limit = Number(args[++i]);
  else if (flag === '--ack-through') options.ackThrough = Number(args[++i]);
  else {
    process.stderr.write('usage: tv-feedback.mjs [--after N] [--limit N] [--ack] | --ack-through N\n');
    process.exit(64);
  }
}

try {
  const client = await recommendationsClient();
  if (options.ackThrough !== undefined) {
    process.stdout.write(`${JSON.stringify(await ackFeedback(options.ackThrough, { client: client ?? undefined }))}\n`);
  } else {
    const page = await fetchFeedback({ client: client ?? undefined, afterSequence: options.afterSequence, limit: options.limit });
    process.stdout.write(`${JSON.stringify(page, null, 2)}\n`);
    if (options.ack && page.events.length) await ackFeedback(page.lastSequence, { client: client ?? undefined });
  }
} catch (error) {
  process.stderr.write(`${JSON.stringify({ error: { code: error.code || 'error', message: error.message } })}\n`);
  process.exit(1);
}
