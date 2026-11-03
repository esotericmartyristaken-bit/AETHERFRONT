import test from 'node:test';
import assert from 'node:assert/strict';
import { shortLeaveReason } from '../src/lmp.js';

test('legacy identity rejection gives a complete player-name instruction', () => {
  assert.equal(shortLeaveReason('Identity account or player name belongs to another key; ask the server owner about account enrollment'), 'Please choose another player name, or contact the server owner.');
});
test('specific identity rejection reasons fit Discord without truncation', () => {
  for (const reason of [
    'Account details are invalid. Please update your client.',
    'Name already in use. Please choose another player name.',
    'Account key changed. Restore your saved key or contact the server owner.',
    'Please choose another player name, or contact the server owner.'
  ]) assert.equal(shortLeaveReason(reason), reason);
});
