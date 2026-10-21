// Standalone media publication only. No application/control channel writes.
import { execFile } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = path.dirname(fileURLToPath(import.meta.url));
const usage = 'node ControlPlane/media-controller.mjs publish --catalog <install-catalog.json> --asset <kesas-name-SHA256.zip> [--asset <archive> ...] --output <MediaReferences.json>';
const quote = value => `'${value.replaceAll("'", "''")}'`;

async function main(args) {
  if (args.length === 1 && ['--help', '-h'].includes(args[0])) { console.log(usage); return; }
  if (args.shift() !== 'publish') throw new Error(usage);
  const options = { assetPaths: [] };
  for (let index = 0; index < args.length; index += 2) {
    const flag = args[index], value = args[index + 1];
    if (!['--catalog','--asset','--output'].includes(flag) || !value || value.startsWith('--') || /[\x00-\x1f]/.test(value)) throw new Error(usage);
    const key = { '--catalog':'catalogPath', '--output':'outputPath' }[flag];
    if (flag === '--asset') options.assetPaths.push(path.resolve(value));
    else if (Object.hasOwn(options, key)) throw new Error('Duplicate argument.');
    else options[key] = path.resolve(value);
  }
  if (!options.catalogPath || !options.outputPath || options.assetPaths.length < 1 || options.assetPaths.length > 1000) throw new Error(usage);
  const input = JSON.stringify(options);
  if (Buffer.byteLength(input) > 1024 * 1024) throw new Error('Media request exceeds 1 MiB.');
  // Static code, JSON over stdin, and argument-array execution. Never interpolate
  // user-provided paths into executable PowerShell or invoke a command shell.
  const script = `$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. ${quote(path.resolve(directory, '../KESAS-VPS-Server/Update.Common.ps1'))}
. ${quote(path.resolve(directory, '../KESAS-VPS-Server/GitHub.Publish.ps1'))}
$request=[Console]::In.ReadToEnd() | ConvertFrom-Json
$document=Publish-GhpReferencedMedia -CatalogPath $request.catalogPath -AssetPaths $request.assetPaths -OutputPath $request.outputPath
$document | ConvertTo-Json -Depth 8 -Compress`;
  await new Promise((resolve, reject) => {
    const child = execFile(process.platform === 'win32' ? 'pwsh.exe' : 'pwsh',
      ['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script, 'utf16le').toString('base64')],
      { windowsHide: true, maxBuffer: 1024 * 1024, timeout: 2 * 60 * 60 * 1000 },
      (error, stdout) => {
        // PowerShell/gh stderr can contain local paths or signed transport URLs.
        if (error) { reject(new Error('Media validation/publication failed. Existing immutable assets preserved. No application channel changed.')); return; }
        try {
          const document = JSON.parse(stdout.trim());
          console.log(JSON.stringify({ ok:true, referencePath:options.outputPath, assetCount:document.assets.length, applicationChannelChanged:false }));
          resolve();
        } catch { reject(new Error('Media publication readback unconfirmed. Retry identical inputs; no application channel changed.')); }
      });
    child.stdin.on('error', () => {});
    child.stdin.end(input);
  });
}

main(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
