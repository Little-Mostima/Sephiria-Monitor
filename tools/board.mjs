#!/usr/bin/env node
// The project's own task board. One JSON file at the repo root, one command to read or change it.
//
// Why not a chat-side list: the board has to survive the session, be readable by the next agent
// (or a human) without any tooling, and be checked by the commit gate. A file in the repo does all
// three; anything living outside the repo does none of them.
//
//   node tools\board.mjs                        print the board
//   node tools\board.mjs show <item>            one item with its full note
//   node tools\board.mjs add "<text>" [--goal g_x]
//   node tools\board.mjs start <item>           mark doing (claims it for this session)
//   node tools\board.mjs done <item> --note "result"
//   node tools\board.mjs blocked <item> --note "why"
//   node tools\board.mjs note <item> --note "text"
//   node tools\board.mjs goal-done <goal> --pointer "commit / path / doc"
//   node tools\board.mjs check                  integrity only (the gate runs this)
//
// <item> is an id (i_ab12cd34) or any unique substring of its text.
import fs from 'node:fs';
import { BOARD } from './config.mjs';

const STATUS = { todo: 'todo', doing: 'doing', done: 'done', blocked: 'blocked' };
const MARK = { todo: ' ', doing: '>', done: 'x', blocked: '!' };

function load() {
  if (!fs.existsSync(BOARD)) fail('board.json missing at ' + BOARD);
  try {
    return JSON.parse(fs.readFileSync(BOARD, 'utf8'));
  } catch (e) {
    fail('board.json is not valid JSON: ' + e.message);
  }
}

function save(board) {
  board.updated = new Date().toISOString().slice(0, 10);
  fs.writeFileSync(BOARD, JSON.stringify(board, null, 2) + '\n', 'utf8');
}

function fail(msg) {
  console.error('board: ' + msg);
  process.exit(1);
}

function allItems(board) {
  return board.goals.flatMap((g) => g.items.map((i) => ({ goal: g, item: i })));
}

function resolve(board, key) {
  if (!key) fail('which item? give an id or part of its text');
  const hits = allItems(board).filter(({ item }) => item.id === key || item.text.toLowerCase().includes(key.toLowerCase()));
  if (!hits.length) fail('no item matches "' + key + '"');
  if (hits.length > 1) fail('"' + key + '" matches ' + hits.length + ': ' + hits.map((h) => h.item.id).join(', '));
  return hits[0];
}

function resolveGoal(board, key) {
  const hits = board.goals.filter((g) => g.id === key || g.title.includes(key));
  if (!hits.length) fail('no goal matches "' + key + '"');
  if (hits.length > 1) fail('"' + key + '" matches ' + hits.length + ' goals');
  return hits[0];
}

function newId(prefix, taken) {
  for (;;) {
    const id = prefix + Math.random().toString(16).slice(2, 10);
    if (!taken.has(id)) return id;
  }
}

function print(board) {
  const out = [board.project + '  (updated ' + board.updated + ')'];
  for (const g of board.goals) {
    const done = g.items.filter((i) => i.status === 'done').length;
    out.push('');
    out.push('目标 ' + g.id + '：' + g.title + ' [' + g.status + ']  ' + done + '/' + g.items.length + ' done');
    if (g.doneWhen) out.push('  怎样算做完：' + g.doneWhen);
    if (g.pointer) out.push('  产出：' + g.pointer);
    for (const i of g.items) {
      if (i.status === 'done' && !process.argv.includes('--all')) continue;
      out.push('  [' + MARK[i.status] + '] ' + i.id + ' ' + i.text + (i.note ? '  — ' + i.note.slice(0, 80) : ''));
    }
  }
  console.log(out.join('\n'));
}

function check(board) {
  const problems = [];
  const ids = new Set();
  for (const g of board.goals) {
    if (!g.id || !g.title) problems.push('goal without id/title');
    if (!g.doneWhen) problems.push(g.id + ': no doneWhen');
    if (!['active', 'done', 'suspended'].includes(g.status)) problems.push(g.id + ': bad status ' + g.status);
    if (g.status === 'done' && !g.pointer) problems.push(g.id + ': done without pointer');
    for (const i of g.items) {
      if (ids.has(i.id)) problems.push('duplicate item id ' + i.id);
      ids.add(i.id);
      if (!STATUS[i.status]) problems.push(i.id + ': bad status ' + i.status);
      if (i.status === 'done' && !i.note) problems.push(i.id + ': done without note');
      if (i.status === 'blocked' && !i.note) problems.push(i.id + ': blocked without reason');
    }
  }
  if (problems.length) {
    console.error('BOARD FAILED\n  ' + problems.join('\n  '));
    process.exit(1);
  }
  console.log('BOARD OK - ' + board.goals.length + ' goal(s), ' + ids.size + ' item(s)');
}

// ---- args ----
const argv = process.argv.slice(2);
const flag = (name) => {
  const i = argv.indexOf('--' + name);
  return i >= 0 ? argv[i + 1] : undefined;
};
const cmd = argv[0] && !argv[0].startsWith('--') ? argv[0] : 'list';
const positional = argv.slice(cmd === 'list' ? 0 : 1).filter((a, i, arr) => !a.startsWith('--') && !(i > 0 && arr[i - 1].startsWith('--')));

const board = load();

switch (cmd) {
  case 'list':
    print(board);
    break;
  case 'show': {
    const { goal, item } = resolve(board, positional[0]);
    console.log(goal.id + ' / ' + item.id + ' [' + item.status + ']');
    console.log(item.text);
    if (item.note) console.log('\n' + item.note);
    break;
  }
  case 'add': {
    const text = positional.join(' ');
    if (!text) fail('add needs the item text');
    const goal = flag('goal') ? resolveGoal(board, flag('goal')) : board.goals.find((g) => g.status === 'active');
    if (!goal) fail('no active goal; pass --goal');
    const id = newId('i_', new Set(allItems(board).map((x) => x.item.id)));
    goal.items.push({ id, text, status: 'todo', note: '' });
    save(board);
    console.log('added ' + id + ' to ' + goal.id);
    break;
  }
  case 'start':
  case 'done':
  case 'blocked':
  case 'todo': {
    const { item } = resolve(board, positional[0]);
    item.status = cmd;
    const note = flag('note');
    if (note !== undefined) item.note = note;
    if (cmd === 'done' && !item.note) fail('done needs --note "what came out of it"');
    if (cmd === 'blocked' && !item.note) fail('blocked needs --note "why"');
    save(board);
    console.log(item.id + ' -> ' + cmd);
    break;
  }
  case 'note': {
    const { item } = resolve(board, positional[0]);
    const note = flag('note');
    if (note === undefined) fail('note needs --note "text"');
    item.note = note;
    save(board);
    console.log(item.id + ' note updated');
    break;
  }
  case 'goal-done': {
    const goal = resolveGoal(board, positional[0]);
    const pointer = flag('pointer');
    if (!pointer) fail('goal-done needs --pointer "commit / path / doc"');
    goal.status = 'done';
    goal.pointer = pointer;
    save(board);
    console.log(goal.id + ' -> done (' + pointer + ')');
    break;
  }
  case 'check':
    check(board);
    break;
  default:
    fail('unknown command "' + cmd + '"');
}
