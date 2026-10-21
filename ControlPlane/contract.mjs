// Public, versioned controller/host contract. Runtime configuration is not part of it.
export const REPOSITORY = 'esotericmartyristaken-bit/AETHERFRONT';
export const CONTRACT = 1;
export const BOOTSTRAP = 1;
export const CAPABILITIES = Object.freeze(['application-v1', 'media-payload-v1', 'control-plane-v1', 'host-report-v1', 'guard-15m-empty-v1', 'atomic-control-rollback-v1', 'native-tools-v1']);
export const MAX_CONTROL_BYTES = 128 * 1024 * 1024;
export const HASH = /^[a-f0-9]{64}$/;
export function requireValue(condition, message) { if (!condition) throw new Error(message); }
export function exact(value, fields, label) {
  requireValue(value && typeof value === 'object' && !Array.isArray(value), `Invalid ${label}`);
  requireValue(Object.keys(value).length === fields.length && Object.keys(value).every(key => fields.includes(key)), `Unsupported ${label} fields`);
}
export function sequence(version) {
  requireValue(typeof version === 'string' && /^control-[1-9][0-9]{0,8}$/.test(version), 'Invalid control version');
  return Number(version.slice(8));
}
export function validateControlDescriptor(value) {
  exact(value, ['schemaVersion','transactionVersion','version','releaseTag','assetName','sha256','sizeBytes','minimumBootstrap'], 'control descriptor');
  sequence(value.version);
  requireValue(value.schemaVersion === 1 && value.transactionVersion === 1 && value.minimumBootstrap === 1, 'Unsupported control contract or bootstrap');
  requireValue(value.releaseTag === `host-${value.version}` && value.assetName === `Aetherfront-Control-${value.version}.zip`, 'Invalid control artifact');
  requireValue(HASH.test(value.sha256) && Number.isSafeInteger(value.sizeBytes) && value.sizeBytes > 0 && value.sizeBytes <= MAX_CONTROL_BYTES, 'Invalid control size or hash');
  return value;
}
export function allowedControlPath(name) {
  return typeof name === 'string' && (
    /^ControlPlane\/(worker|engine|contract|github|public-status)\.mjs$/.test(name) ||
    name==='ControlPlane/Read-ClientInventory.ps1' ||
    /^APFDiscordBot\/src\/[a-z][a-z0-9-]*\.js$/.test(name) ||
    /^APFDiscordBot\/package(?:-lock)?\.json$/.test(name) ||
    /^APFDiscordBot\/scripts\/(Verify-KesasRelease|Stop-KesasGracefully|Stop-APFLmpGracefully)\.ps1$/.test(name) ||
    /^KESAS-VPS-Server\/(Publish-Release|Update.Common|GitHub.Publish|Export-Client|Start-Server|Stop-Server)\.ps1$/.test(name)
  );
}
export const REQUIRED_CONTROL_FILES = Object.freeze([
  ...['worker','engine','contract','github','public-status'].map(name => `ControlPlane/${name}.mjs`),
  'ControlPlane/Read-ClientInventory.ps1',
  'APFDiscordBot/package.json','APFDiscordBot/package-lock.json',
  ...['index','config','update-manager','github-deployment','lmp'].map(name => `APFDiscordBot/src/${name}.js`),
  'APFDiscordBot/src/native-broker.js',
  'APFDiscordBot/scripts/Verify-KesasRelease.ps1',
  ...['Publish-Release','Update.Common','GitHub.Publish','Export-Client','Start-Server','Stop-Server'].map(name => `KESAS-VPS-Server/${name}.ps1`)
]);
export function validateControlManifest(value, descriptor) {
  exact(value, ['schemaVersion','transactionVersion','version','runtime','capabilities','files'], 'control manifest');
  requireValue(value.schemaVersion === 1 && value.transactionVersion === 1 && value.version === descriptor.version, 'Unsupported control manifest');
  exact(value.runtime, ['nodeMajor','powerShellMajor','dotnetMajor'], 'runtime requirements');
  requireValue(value.runtime.nodeMajor === 24 && value.runtime.powerShellMajor === 7 && value.runtime.dotnetMajor === 8, 'Unsupported runtime provisioning requirement');
  requireValue(Array.isArray(value.capabilities) && value.capabilities.length === CAPABILITIES.length && CAPABILITIES.every(capability => value.capabilities.includes(capability)), 'Unsupported capabilities');
  requireValue(Array.isArray(value.files) && value.files.length >= REQUIRED_CONTROL_FILES.length && value.files.length <= 200, 'Invalid control inventory');
  const seen = new Set();
  let total = 0;
  for (const file of value.files) {
    exact(file, ['path','sha256','sizeBytes'], 'control file');
    requireValue(allowedControlPath(file.path) && !seen.has(file.path.toLowerCase()) && HASH.test(file.sha256) && Number.isSafeInteger(file.sizeBytes) && file.sizeBytes >= 0, 'Invalid control file');
    total += file.sizeBytes;
    seen.add(file.path.toLowerCase());
  }
  requireValue(total <= MAX_CONTROL_BYTES && REQUIRED_CONTROL_FILES.every(name => seen.has(name.toLowerCase())), 'Incomplete or oversized control payload');
  return value;
}
const VERSION = /^(?:kesas-rc[1-9][0-9]{0,14}|control-[1-9][0-9]{0,8})$/;
const STATUSES = ['healthy','accepted','waiting-existing-guard','staging','applying','rolled-back','blocked','idle','unknown'];
function nullableVersion(value) { return value === null || (typeof value === 'string' && VERSION.test(value)); }
function safeCode(value) { return value === null || (typeof value === 'string' && /^[a-z][a-z0-9-]{0,79}$/.test(value)); }
function validDate(value) { return typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(value) && Number.isFinite(Date.parse(value)); }
export function validateHostReport(value) {
  exact(value, ['schemaVersion','contractVersion','bootstrapVersion','observedAt','expiresAt','capabilities','control','application','components','runtime'], 'host report');
  requireValue(value.schemaVersion === 1 && value.contractVersion === 1 && value.bootstrapVersion === 1 && validDate(value.observedAt) && validDate(value.expiresAt), 'Unsupported host report');
  requireValue(Array.isArray(value.capabilities) && new Set(value.capabilities).size === value.capabilities.length && value.capabilities.every(item => CAPABILITIES.includes(item)), 'Invalid host capabilities');
  exact(value.control, ['version','sequence','sha256','status','phase','pendingVersion','lastSuccess','rollback','failure'], 'control status');
  requireValue(HASH.test(value.control.sha256), 'Invalid installed control hash');
  requireValue(value.control.version === `control-${value.control.sequence}` && Number.isSafeInteger(value.control.sequence) && value.control.sequence > 0 && STATUSES.includes(value.control.status) && safeCode(value.control.phase) && nullableVersion(value.control.pendingVersion) && nullableVersion(value.control.lastSuccess) && nullableVersion(value.control.rollback) && safeCode(value.control.failure), 'Invalid control status');
  exact(value.application, ['clientVersion','packVersion','status','phase','pendingVersion','health','lastSuccess','failure'], 'application status');
  requireValue(nullableVersion(value.application.clientVersion) && nullableVersion(value.application.packVersion) && nullableVersion(value.application.pendingVersion) && nullableVersion(value.application.lastSuccess) && STATUSES.includes(value.application.status) && safeCode(value.application.phase) && ['healthy','offline','unknown'].includes(value.application.health) && safeCode(value.application.failure), 'Invalid application status');
  const labels = ['watcher','nativeRunner','publisher','verifier','packService','serverRuntime','serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'];
  exact(value.components, labels, 'component hashes');
  requireValue(labels.every(key => value.components[key] === null || HASH.test(value.components[key])), 'Invalid component hash');
  exact(value.runtime, ['node','powershell','dotnet'], 'host runtimes');
  requireValue(Object.values(value.runtime).every(item => typeof item === 'string' && /^\d+\.\d+\.\d+(?:\.\d+)?$/.test(item)), 'Invalid runtime versions');
  return value;
}
export function assertCapabilities(report, requirements = []) {
  validateHostReport(report);
  const now = Date.now();
  requireValue(Date.parse(report.observedAt) <= now + 5000 && Date.parse(report.expiresAt) > now && Date.parse(report.expiresAt) - Date.parse(report.observedAt) <= 180000, 'Host capability report is stale or invalid');
  requireValue(Array.isArray(requirements) && requirements.every(item => CAPABILITIES.includes(item)), 'Unknown minimum capability');
  const missing = requirements.filter(item => !report.capabilities.includes(item));
  requireValue(missing.length === 0, `Host lacks required capabilities: ${missing.join(',')}`);
  const recovered = ['rolled-back','blocked'].includes(report.control.status) && !report.control.pendingVersion && report.control.lastSuccess === report.control.version && report.application.health === 'healthy';
  requireValue(['healthy','idle'].includes(report.control.status) || recovered, 'Host control plane is not healthy');
  requireValue(report.control.lastSuccess===report.control.version, 'Installed control version lacks completed health proof');
  return report;
}
