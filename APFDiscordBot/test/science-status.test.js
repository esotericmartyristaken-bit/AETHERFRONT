import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { UniverseManager } from '../src/universe.js';
import { publicStatusEmbed } from '../src/bot.js';

function state(earned = [400, 168.055976], starting = 0) {
  return {
    Revision: 1,
    Accounts: [...earned.map((_, i) => ({ AccountId: `account-${i}` })), { AccountId: 'new-account' }],
    PersonalProgression: {
      Version: 1, StartingScience: starting,
      TotalEarnedScience: earned.reduce((sum, amount) => sum + amount, 0),
      Players: earned.map((amount, i) => ({
        AccountId: `account-${i}`, EarnedScience: amount, SharedScienceCredited: 0,
        Science: 0, TradableScience: 0,
        ScienceSources: amount ? [{ Id: `award:source-${i}`, CreditedScience: amount, CumulativeScience: amount }] : []
      }))
    }
  };
}

async function fixture(t, personal = true) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-science-status-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const serverRoot = path.join(root, 'Server');
  const universeRoot = path.join(serverRoot, 'Universe');
  const scenario = path.join(universeRoot, 'Scenarios');
  const file = path.join(serverRoot, 'Aetherfront', 'state.json');
  await fs.mkdir(scenario, { recursive: true });
  await fs.mkdir(path.dirname(file), { recursive: true });
  await fs.writeFile(path.join(scenario, 'Funding.txt'), 'funds = 1234.5\n');
  await fs.writeFile(path.join(scenario, 'ResearchAndDevelopment.txt'), 'sci = 0\n');
  const config = {
    lmp: { serverRoot, ...(personal ? { deploymentRoot: root } : {}) },
    universe: { universeRoot, backupRoot: path.join(root, 'Backups'), exportRoot: path.join(root, 'Exports') }
  };
  return {
    config, file, manager: new UniverseManager(config),
    write: value => fs.writeFile(file, typeof value === 'string' ? value : JSON.stringify(value))
  };
}

function field(stats) {
  return publicStatusEmbed({ online: true, ready: true, playerCount: 0, settings: { name: 'KESAS' } },
    null, Date.now(), stats).toJSON().fields.find(value => value.name === 'Last backup').value;
}

test('personal science is half all earned science, with exact authoritative decimal baseline', async t => {
  const f = await fixture(t);
  await f.write(state());
  const before = await fs.readFile(f.file);
  const stats = await f.manager.careerStats();
  assert.deepEqual(stats, { funds: 1234.5, science: '284.027988', scienceBasis: 'new-player-baseline' });
  assert.match(field(stats), /\*\*Funds · New-player science\*\*\n1,235 · 284\.027988$/);
  assert.deepEqual(await fs.readFile(f.file), before, 'status inspection must not modify the ledger');
});

test('spent wallets and already credited shared science do not inflate or reduce the baseline', async t => {
  const f = await fixture(t), ledger = state();
  ledger.PersonalProgression.Players[0].SharedScienceCredited = 60;
  ledger.PersonalProgression.Players[1].SharedScienceCredited = 100;
  ledger.PersonalProgression.Players[0].Science = 1;
  ledger.PersonalProgression.Players[1].Science = 9;
  await f.write(ledger);
  assert.equal((await f.manager.careerStats()).science, '284.027988');
});

test('starting science is included once in the new-player total baseline', async t => {
  const f = await fixture(t);
  await f.write(state([100, 50], 10));
  assert.equal((await f.manager.careerStats()).science, '85');
});

test('consistent zero earned science remains genuine zero, not Unknown', async t => {
  const f = await fixture(t);
  await f.write(state([]));
  const stats = await f.manager.careerStats();
  assert.equal(stats.science, '0');
  assert.match(field(stats), /New-player science\*\*\n1,235 · 0$/);
});

test('missing personal state displays Unknown, never the legacy scenario zero', async t => {
  const f = await fixture(t);
  const stats = await f.manager.careerStats();
  assert.equal(stats.science, null);
  assert.equal(stats.funds, 1234.5);
  assert.match(field(stats), /New-player science\*\*\n1,235 · Unknown$/);
});

test('malformed and invalid personal states fail closed', async t => {
  const f = await fixture(t);
  for (const invalid of ['{', '{}', { ...state(), PersonalProgression: null }]) {
    await f.write(invalid);
    assert.equal((await f.manager.careerStats()).science, null);
  }
});

test('inconsistent totals, sources and shared credits display Unknown', async t => {
  const f = await fixture(t);
  for (const invalidate of [
    ledger => { ledger.PersonalProgression.TotalEarnedScience = 999; },
    ledger => { ledger.PersonalProgression.Players[0].ScienceSources[0].CreditedScience = 399; },
    ledger => { ledger.PersonalProgression.Players[0].SharedScienceCredited = 999; },
    ledger => { ledger.PersonalProgression.Players[0].Science = -1; }
  ]) {
    const ledger = state(); invalidate(ledger); await f.write(ledger);
    assert.equal((await f.manager.careerStats()).science, null);
  }
});

test('personal baseline refresh follows new awards and missing state immediately', async t => {
  const f = await fixture(t);
  await f.write(state([100]));
  assert.equal((await f.manager.careerStats()).science, '50');
  await f.write(state([200]));
  assert.equal((await f.manager.careerStats()).science, '100');
  await fs.unlink(f.file);
  assert.equal((await f.manager.careerStats()).science, null);
});

test('mismatched configured deployment cannot read another server ledger', async t => {
  const f = await fixture(t);
  await f.write(state());
  f.config.lmp.serverRoot = path.join(path.dirname(f.config.lmp.serverRoot), 'OtherServer');
  assert.equal((await f.manager.careerStats()).science, null);
});

test('legacy/non-KESAS scenarios and their public label remain unchanged', async t => {
  const f = await fixture(t, false);
  await fs.writeFile(path.join(f.manager.root, 'Scenarios', 'ResearchAndDevelopment.txt'), 'sci = 261.1361\n');
  await f.write(state());
  const stats = await f.manager.careerStats();
  assert.deepEqual(stats, { funds: 1234.5, science: 261.1361 });
  assert.match(field(stats), /\*\*Funds · Science\*\*\n1,235 · 261\.1$/);
});
