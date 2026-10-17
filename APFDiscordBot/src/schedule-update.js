import { loadConfig } from './config.js';
import { UpdateManager } from './update-manager.js';

function option(name) {
  const index = process.argv.indexOf(name);
  return index >= 0 ? process.argv[index + 1] : '';
}

const id = option('--id');
if (!id) {
  console.error('Usage: npm run schedule:update -- --id <id> [--minutes 15] [--changelog <text>] [--by <operator>]');
  process.exitCode = 2;
} else {
  const config = await loadConfig();
  const manager = new UpdateManager(config, null, null);
  const result = await manager.queue(id, option('--by') || 'local-operator', {
    countdownMinutes: option('--minutes') || undefined,
    changelog: option('--changelog')
  });
  console.log(JSON.stringify(result, null, 2));
  if (!result.ok) process.exitCode = 1;
}
