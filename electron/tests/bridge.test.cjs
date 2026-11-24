'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');
const { setTimeout: delay } = require('node:timers/promises');
const { test } = require('node:test');
const Bridge = require('../bridge.cjs');

const binary = path.resolve(__dirname, '../../bin/CodexLimitsBridge.exe');

function temporary(t) {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-limits-test-'));
  const children = [];
  t.after(async () => {
    for (const { bridge, closed } of children) bridge.close();
    await Promise.all(children.map(({ closed }) => closed));
    fs.rmSync(folder, { recursive: true, force: true, maxRetries: 3 });
  });
  return { folder, children };
}

function launch(children, executable, folder, mode, legacy, env) {
  const bridge = new Bridge(executable, [String(process.pid), folder, mode, process.execPath, ...legacy], env);
  children.push({ bridge, closed: once(bridge.child, 'close') });
  return bridge;
}

function read(folder, name) {
  return JSON.parse(fs.readFileSync(path.join(folder, name), 'utf8'));
}

test('Demo reads both quotas and persists a settings patch without overwriting other values', async t => {
  const { folder: base, children } = temporary(t);
  const folder = path.join(base, 'data');
  const bridge = launch(children, binary, folder, 'demo', []);
  const { settings } = await bridge.request('settings');
  const { snapshot, demo, busy } = await bridge.request('tick');
  assert.equal(demo, true);
  assert.equal(busy, false);
  assert.equal(snapshot.FiveHour.Remaining, 60);
  assert.equal(snapshot.Weekly.Remaining, 80);
  assert.equal(snapshot.ResetCredits, 2);
  assert.equal(settings.DisplayId, null);

  const saved = await bridge.request('save', { patch: { AccentRgb: 0x84e4c4, BarThickness: 99, GameExclusions: ['game.exe'], DisplayId: 4294967421 } });
  assert.equal(saved.AccentRgb, 0x84e4c4);
  assert.equal(saved.BarThickness, 7);
  assert.equal(saved.HistoryDays, settings.HistoryDays);
  assert.deepEqual(saved.GameExclusions, ['game.exe']);
  assert.deepEqual(read(folder, 'settings.json'), saved);
  assert.equal(saved.DisplayId, 4294967421);
  const reopened = launch(children, binary, folder, 'demo', []);
  assert.equal((await reopened.request('settings')).settings.DisplayId, 4294967421);
  assert.equal((await reopened.request('save', { patch: { DisplayId: null } })).DisplayId, null);
});

test('Existing WinForms data migrates once and cleared history stays cleared', async t => {
  const { folder: base, children } = temporary(t);
  const folder = path.join(base, 'new');
  const legacy = path.join(base, 'old');
  const now = Math.floor(Date.now() / 1000);
  fs.mkdirSync(legacy);
  fs.writeFileSync(path.join(legacy, 'settings.json'), JSON.stringify({ AccentRgb: 0xabcdef, HistoryEnabled: true }));
  const oldSample = { At: now - 1200, Five: 51, Week: 61, FiveReset: now + 5000, WeekReset: now + 100000 };
  fs.writeFileSync(path.join(legacy, 'history.json'), JSON.stringify([oldSample]));

  const bridge = launch(children, binary, folder, 'live', [legacy]);
  assert.equal((await bridge.request('settings')).settings.AccentRgb, 0xabcdef);
  assert.deepEqual((await bridge.request('history', { days: 1 })).samples, [oldSample]);
  await bridge.request('clear');
  assert.deepEqual((await bridge.request('history', { days: 1 })).samples, []);

  const reopened = launch(children, binary, folder, 'live', [legacy]);
  assert.deepEqual((await reopened.request('history', { days: 1 })).samples, []);
  assert.deepEqual(read(legacy, 'history.json'), [oldSample]);
});

test('Live polling uses the Codex app-server protocol and records matching quota windows', async t => {
  const { folder: base, children } = temporary(t);
  const folder = path.join(base, 'data');
  const cliHome = path.join(base, 'cli-home');
  const testBridge = path.join(base, 'CodexLimitsBridge.exe');
  fs.copyFileSync(binary, testBridge);
  fs.writeFileSync(path.join(base, 'app-server'), [
    "const readline = require('node:readline');",
    "const io = readline.createInterface({ input: process.stdin });",
    "let initialized = false;",
    "io.on('line', line => {",
    "  const request = JSON.parse(line);",
    "  if (request.method === 'initialized') initialized = true;",
    "  if (request.method === 'initialize') console.log(JSON.stringify({ id: request.id, result: {} }));",
    "  if (request.method === 'account/read' && initialized) console.log(JSON.stringify({ id: request.id, result: { account: { type: 'chatgpt' } } }));",
    "  if (request.method === 'account/rateLimits/read' && initialized) {",
    "    const rateLimitsByLimitId = { codex: { primary: { windowDurationMins: 10080, usedPercent: 39 }, secondary: { windowDurationMins: 300, usedPercent: 11 } } };",
    "    console.log(JSON.stringify({ id: request.id, result: { rateLimitsByLimitId } }));",
    "  }",
    "});"
  ].join('\n'));
  const bridge = launch(children, testBridge, folder, 'live', [], { ...process.env, CODEX_USAGE_CLI: process.execPath, CODEX_HOME: cliHome });
  await bridge.request('save', { patch: { HistoryEnabled: true } });
  let state = await bridge.request('refresh');
  for (let attempt = 0; !state.snapshot && attempt < 30; attempt++) {
    await delay(100);
    state = await bridge.request('tick');
  }
  assert.ok(state.snapshot, state.status);
  assert.equal(state.loginRequired, false);
  assert.equal(state.snapshot.FiveHour.Remaining, 89);
  assert.equal(state.snapshot.Weekly.Remaining, 61);
  assert.deepEqual(read(folder, 'history.json').map(row => [row.Five, row.Week]), [[89, 61]]);
});
test('A failed CLI request retains the newest shared quota from the session journal', async t => {
  const { folder: base, children } = temporary(t);
  const folder = path.join(base, 'data');
  const cliHome = path.join(base, 'cli-home');
  const testBridge = path.join(base, 'CodexLimitsBridge.exe');
  fs.copyFileSync(binary, testBridge);
  fs.writeFileSync(path.join(base, 'app-server'), 'process.exitCode = 1;\n');
  const now = new Date();
  const day = now.toISOString().slice(0, 10).split('-');
  const sessions = path.join(cliHome, 'sessions', ...day);
  fs.mkdirSync(sessions, { recursive: true });
  const event = {
    timestamp: now.toISOString(),
    type: 'event_msg',
    payload: { type: 'token_count', rate_limits: {
      limit_id: 'codex', primary: { window_minutes: 300, used_percent: 26 },
      secondary: { window_minutes: 10080, used_percent: 64 }
    } }
  };
  const modelEvent = JSON.parse(JSON.stringify(event));
  modelEvent.payload.rate_limits.limit_id = 'model-specific';
  modelEvent.payload.rate_limits.primary.used_percent = 100;
  fs.writeFileSync(path.join(sessions, 'rollout-test.jsonl'), [event, modelEvent].map(row => JSON.stringify(row)).join('\n') + '\n');
  const bridge = launch(children, testBridge, folder, 'live', [], { ...process.env, CODEX_USAGE_CLI: process.execPath, CODEX_HOME: cliHome });
  let state = await bridge.request('refresh');
  for (let attempt = 0; (!state.snapshot || state.busy) && attempt < 30; attempt++) {
    await delay(100);
    state = await bridge.request('tick');
  }
  assert.ok(state.snapshot);
  assert.equal(state.snapshot.FromSessionLog, true);
  assert.equal(state.snapshot.FiveHour.Remaining, 74);
  assert.equal(state.snapshot.Weekly.Remaining, 36);
  assert.equal(state.stale, true);
  assert.ok(state.status);
});
test('Unsigned and API-key-only accounts request ChatGPT sign-in before reading limits', async t => {
  const { folder: base, children } = temporary(t);
  const testBridge = path.join(base, 'CodexLimitsBridge.exe');
  fs.copyFileSync(binary, testBridge);
  fs.writeFileSync(path.join(base, 'app-server'), [
    "const io = require('node:readline').createInterface({ input: process.stdin });",
    "io.on('line', line => {",
    "  const request = JSON.parse(line);",
    "  if (request.method === 'initialize') console.log(JSON.stringify({ id: request.id, result: {} }));",
    "  if (request.method === 'account/read') {",
    "    const account = process.env.WIDGET_TEST_ACCOUNT === 'key' ? { type: 'apiKey' } : null;",
    "    console.log(JSON.stringify({ id: request.id, result: { account, requiresOpenaiAuth: true } }));",
    "  }",
    "  if (request.method === 'account/rateLimits/read') require('node:fs').writeFileSync('unexpected-quota', '1');",
    "});"
  ].join('\n'));

  for (const mode of ['signed-out', 'key']) {
    const bridge = launch(children, testBridge, path.join(base, mode), 'live', [], {
      ...process.env, CODEX_USAGE_CLI: process.execPath, CODEX_HOME: path.join(base, 'cli-home'), WIDGET_TEST_ACCOUNT: mode
    });
    let state = await bridge.request('refresh');
    for (let attempt = 0; !state.loginRequired && attempt < 30; attempt++) {
      await delay(100);
      state = await bridge.request('tick');
    }
    assert.equal(state.loginRequired, true, mode);
    assert.match(state.status, /ChatGPT/);
    assert.equal(state.snapshot, null);
  }
  assert.equal(fs.existsSync(path.join(base, 'unexpected-quota')), false);
});
