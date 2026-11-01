#!/usr/bin/env node
// Optional stdio -> HTTP shim.
//
// The plugin itself is the MCP server: while Sephiria runs, POST http://127.0.0.1:47901/mcp
// speaks JSON-RPC (Streamable HTTP, JSON responses). Clients that can only speak stdio
// (Claude Desktop, older CLIs) can point at this file instead.
//
//   node tools/mcp-stdio.mjs
//
// Env: SEPHIRIA_BRIDGE (default http://127.0.0.1:47901)

const BRIDGE = (process.env.SEPHIRIA_BRIDGE || 'http://127.0.0.1:47901').replace(/\/$/, '');
const URL_ = BRIDGE + '/mcp';

async function rpc(obj) {
  const res = await fetch(URL_, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
    body: JSON.stringify(obj),
  });
  if (res.status === 202) return null;
  const text = await res.text();
  if (!text.trim()) return null;
  return JSON.parse(text);
}

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

const offline = (id) => ({
  jsonrpc: '2.0',
  id,
  error: {
    code: -32000,
    message: `Sephiria bridge is not reachable at ${BRIDGE}. Start the game (the plugin listens on 47901) and retry.`,
  },
});

let buffer = '';
let chain = Promise.resolve();

function handleLine(line) {
  line = line.trim();
  if (!line) return;
  let msg;
  try {
    msg = JSON.parse(line);
  } catch (e) {
    send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'parse error' } });
    return;
  }
  chain = chain
    .then(() => rpc(msg))
    .then((answer) => {
      if (answer) send(answer);
    })
    .catch(() => {
      if (msg && msg.id !== undefined) send(offline(msg.id));
    });
}

process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let nl;
  while ((nl = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, nl);
    buffer = buffer.slice(nl + 1);
    handleLine(line);
  }
});
process.stdin.on('end', () => {
  if (buffer.trim()) handleLine(buffer);
  chain.then(() => process.exit(0));
});
