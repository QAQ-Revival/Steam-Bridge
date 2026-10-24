// Builds the QAQR JSON host from the same core as the standalone Steam Bridge.
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

function build({ output = path.join(__dirname, 'build/qaqr'), test = false, testOutput = path.join(__dirname, 'build/tests') } = {}) {
  output = path.resolve(output);
  testOutput = path.resolve(testOutput);
  const compiler = path.join(process.env.WINDIR || 'C:/Windows', 'Microsoft.NET/Framework64/v4.0.30319/csc.exe');
  if (!fs.existsSync(compiler)) throw Error('Windows x64 with .NET Framework 4.8 is required to build the Steam bridge');
  fs.mkdirSync(output, { recursive: true });
  const standalone = new Set(['Program.cs', 'AssemblyInfo.cs', 'MainForm.cs', 'FluentControls.cs']);
  const sources = fs.readdirSync(path.join(__dirname, 'src')).filter(file => file.endsWith('.cs') && !standalone.has(file)).sort().map(file => path.join(__dirname, 'src', file));
  sources.push(path.join(__dirname, 'qaqr/Program.cs'), path.join(__dirname, 'qaqr/AssemblyInfo.cs'));
  const common = ['/nologo', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', '/define:QAQR_HOST', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Management.dll'];
  function compile(args) {
    const result = spawnSync(compiler, [...common, ...args, ...sources], { encoding: 'utf8', windowsHide: true });
    if (result.error || result.status !== 0) throw result.error || Error(result.stdout || result.stderr || 'Steam bridge build failed');
  }
  compile(['/target:winexe', '/main:EndfieldSteamBridge.Program', '/out:' + path.join(output, 'steam-bridge.exe')]);
  for (const name of ['LICENSE', 'SOURCES.txt']) fs.copyFileSync(path.join(__dirname, name), path.join(output, name));
  fs.writeFileSync(path.join(output, 'steam-bridge.exe.config'), '<?xml version="1.0"?><configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup></configuration>');
  if (test) {
    fs.mkdirSync(testOutput, { recursive: true });
    const executable = path.join(testOutput, 'CoreTests.exe');
    compile(['/target:exe', '/main:EndfieldSteamBridge.CoreTests', '/out:' + executable, path.join(__dirname, 'tests/CoreTests.cs')]);
    const result = spawnSync(executable, [], { encoding: 'utf8', windowsHide: true, timeout: 90000 });
    process.stdout.write(result.stdout || '');
    if (result.error || result.status !== 0) throw result.error || Error(result.stderr || 'Steam bridge tests failed');
  }
  console.log('Built the QAQR Steam bridge');
}

if (require.main === module) build({ test: process.argv.includes('--test') });
module.exports = { build };
