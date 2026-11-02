#!/usr/bin/env node
// Scaffold a new AddOns mod: idea -> buildable, deployed, testable skeleton in one command.
//
//   node tools/new-mod.mjs PocketMap
//   node tools/new-mod.mjs PocketMap --id com.mostima.pocketmap --author Mostima
//
// Produces work/pocket-map/ (never committed: work/ is gitignored) with:
//   metadata.json (flat AddOns schema)  mod.json (runner deploy manifest)
//   build.ps1 + src/<Name>.csproj       src/<Name>Mod.cs (HorayModBase skeleton)
//   tests/smoke.suite.json              workshop.json (workshop page fields)
// Next:  cd work/<name>; .\build.ps1   then   node tools/preflight.mjs work/<name>
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const raw = args.find((a) => !a.startsWith('--'));
if (!raw) {
  console.error('usage: node tools/new-mod.mjs <Name> [--id com.mostima.<name>] [--author <name>]');
  process.exit(2);
}
const flag = (name, dflt) => {
  const i = args.indexOf('--' + name);
  return i >= 0 && args[i + 1] ? args[i + 1] : dflt;
};

// PocketMap -> Pascal "PocketMap", folder "pocket-map", id "com.mostima.pocketmap"
const Name = raw.replace(/[^A-Za-z0-9]/g, '');
if (!Name) { console.error('name must contain letters or digits'); process.exit(2); }
const Pascal = Name[0].toUpperCase() + Name.slice(1);
const folder = Pascal.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
const modId = flag('id', 'com.mostima.' + folder.replace(/-/g, ''));
const author = flag('author', 'Mostima');
const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');
const dir = path.join(REPO, 'work', folder);
if (fs.existsSync(dir)) { console.error(`work/${folder} already exists`); process.exit(1); }

const write = (rel, text) => {
  const p = path.join(dir, rel);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, text.endsWith('\n') ? text : text + '\n', 'utf8');
};

write('metadata.json', JSON.stringify({
  modName: Pascal,
  modVersion: '0.1.0',
  modAuthor: author,
  dllFile: `${Pascal}.dll`,
  entryClass: `${Pascal}.${Pascal}Mod`,
}, null, 2));

write('mod.json', JSON.stringify({
  name: folder,
  autoDeploy: true,
  deploy: { addons: [{ src: 'dist', folder: Pascal }] },
}, null, 2));

write('src/' + Pascal + '.csproj', `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <AssemblyName>${Pascal}</AssemblyName>
    <RootNamespace>${Pascal}</RootNamespace>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <NoWarn>CS0436</NoWarn>
    <OutputPath>..\\dist\\</OutputPath>
    <DebugType>none</DebugType>
  </PropertyGroup>
  <PropertyGroup>
    <GameDir Condition="'$(SEPHIRIA_GAME_DIR)' != ''">$(SEPHIRIA_GAME_DIR)</GameDir>
    <GameDir Condition="'$(GameDir)' == '' AND Exists('D:\\steam\\steamapps\\common\\Sephiria\\Sephiria_Data\\Managed')">D:\\steam\\steamapps\\common\\Sephiria</GameDir>
    <Managed>$(GameDir)\\Sephiria_Data\\Managed</Managed>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="0Harmony"><HintPath>$(Managed)\\0Harmony.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Assembly-CSharp"><HintPath>$(Managed)\\Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Mirror"><HintPath>$(Managed)\\Mirror.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine"><HintPath>$(Managed)\\UnityEngine.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine.CoreModule"><HintPath>$(Managed)\\UnityEngine.CoreModule.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>`);

write('build.ps1', `param([switch]$NoDeploy)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = if ($env:SEPHIRIA_DOTNET) { $env:SEPHIRIA_DOTNET } else { "D:\\AscensionMod\\_work\\dotnet\\dotnet.exe" }
$game = if ($env:SEPHIRIA_GAME_DIR) { $env:SEPHIRIA_GAME_DIR } else { "D:\\steam\\steamapps\\common\\Sephiria" }
$env:SEPHIRIA_GAME_DIR = $game
& $dotnet build (Join-Path $here "src\\${Pascal}.csproj") -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }
New-Item -ItemType Directory -Force -Path (Join-Path $here "dist") | Out-Null
Copy-Item (Join-Path $here "metadata.json") (Join-Path $here "dist") -Force
if (-not $NoDeploy) {
  $dst = Join-Path $game "AddOns\\${Pascal}"
  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  Copy-Item (Join-Path $here "dist\\*") $dst -Force
  Write-Host "deployed to $dst" -ForegroundColor Green
}`);

write('src/' + Pascal + 'Mod.cs', `using System;
using UnityEngine;

namespace ${Pascal}
{
    /// <summary>
    /// TODO: one sentence on what this changes and why the player notices.
    /// Loaded only when MainWorld appears; do not touch anything in the constructor.
    /// </summary>
    public class ${Pascal}Mod : HorayModBase
    {
        internal const string Tag = "[${Pascal}]";
        private static bool _done;

        protected override void OnModLoaded()
        {
            HorayModAPI.OnPlayerAvatarSpawnedServerside += OnAvatar;
            Debug.Log(Tag + " loaded v" + metadata.modVersion);
        }

        protected override void OnModUnloaded()
        {
            HorayModAPI.OnPlayerAvatarSpawnedServerside -= OnAvatar;
            Debug.Log(Tag + " unloaded");
        }

        private static void OnAvatar(UnitAvatar avatar)
        {
            if (_done) return;
            _done = true;
            // TODO: the actual behaviour. Read state through public APIs where possible;
            // patch private methods with Harmony only when there is no public seam.
            Debug.Log(Tag + " avatar ready: " + (avatar == null ? "null" : avatar.name));
        }
    }
}`);

write('tests/smoke.suite.json', JSON.stringify({
  name: 'smoke',
  note: `最小回归：加载 ${Pascal} 后游戏能进世界、mod 打日志、关键观测点可读。把 TODO 换成真正的断言。`,
  steps: [
    { restart: true },
    { expect: { path: '/health', field: 'scene', op: '==', value: 'MainWorld', timeoutMs: 240000 } },
    { log: { n: 600, contains: `[${Pascal}] loaded` } },
    { log: { n: 600, contains: `[${Pascal}] avatar ready` } },
    { expect: { path: '/state', field: 'hp', op: '>', value: 0 } },
  ],
}, null, 2));

write('workshop.json', JSON.stringify({
  modId,
  title: { 'zh-CN': Pascal, 'en-US': Pascal },
  description: { 'zh-CN': 'TODO: 一句话说清它改了什么。', 'en-US': 'TODO: one sentence.' },
  tags: ['QoL'],
  author,
  preview: 'thumbnail.png',
  multiplayer: { installation: 'everyone' },
}, null, 2));

write('README.md', `# ${Pascal}

- 目标：TODO
- 观测点：TODO（能不能用 /state /objects /inventory 看到结果？看不到就先加读点）
- 套件：\`tests/smoke.suite.json\`

\`\`\`powershell
.\\build.ps1                                        # 编译 + 装进 AddOns
node ..\\..\\tools\\preflight.mjs work\\${folder}       # 静态 → 编译 → 打包 → 套件
\`\`\`
`);

console.log(`scaffolded work/${folder}`);
console.log(`  entry   ${Pascal}.${Pascal}Mod   modId ${modId}`);
console.log('next:');
console.log(`  cd work/${folder} ; .\\build.ps1`);
console.log(`  node tools/preflight.mjs work/${folder}`);
console.log('  node tools/new-mod.mjs is idempotent-safe: it refuses to overwrite an existing folder');
