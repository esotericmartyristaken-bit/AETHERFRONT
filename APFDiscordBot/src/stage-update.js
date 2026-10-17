import { loadConfig } from './config.js';
import { UpdateManager } from './update-manager.js';

function option(name) {
  const index = process.argv.indexOf(name);
  return index >= 0 ? process.argv[index + 1] : '';
}

const source = option('--source');
if (!source) {
  console.error('Usage: npm run stage:update -- --source <directory> [--id <id>] [--version <version>] [--notes <text>]');
  process.exitCode = 2;
} else {
  const config = await loadConfig();
  const manager = new UpdateManager(config, null, null);
  const manifest = await manager.stage(source, { id: option('--id'), version: option('--version'), notes: option('--notes') });
  console.log(JSON.stringify({ ok: true, id: manifest.id, version: manifest.version, files: manifest.files.length, stagingRoot: manager.stagingRoot }, null, 2));
}

