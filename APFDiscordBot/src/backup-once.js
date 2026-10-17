import { loadConfig } from './config.js';
import { UniverseManager } from './universe.js';

const config = await loadConfig();
const reason = process.env.APF_BACKUP_REASON || 'scheduled-task';
const result = await new UniverseManager(config).createBackup(reason);
console.log(JSON.stringify(result, null, 2));
