#!/usr/bin/env node
// Scaffold a new AddOns mod: idea -> buildable, deployed, testable skeleton in one command.
//
//   node tools/new-mod.mjs PocketMap
//   node tools/new-mod.mjs PocketMap --id com.mostima.pocketmap --author Mostima
//
// Produces work/pocket-map/ (never committed: work/ is gitignored) with:
//   metadata.json (flat AddOns schema)  mod.json (runner deploy manifest)
//   src/<Name>.csproj + <Name>Mod.cs    (公共配置在 work/Directory.Build.props；构建用 tools/build-mod.mjs)
//   tests/smoke.suite.json              workshop.json (workshop page fields)
// Next:  node tools/build-mod.mjs <name> --deploy   then   node tools/preflight.mjs work/<name>
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const raw = args.find((a) => !a.startsWith('--'));
if (!raw) {
  console.error('usage: node tools/new-mod.mjs <Name> [--id com.mostima.<name>] [--author <name>] [--kind plugin|content]');
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

const kind = flag('kind', 'plugin');

// --kind content: copy the multi-content package template (potions + charm + weapon + tablet,
// Localization module, regression suite) and rebrand it. Template lives in the docs checkout,
// not in this repo, because work/ is local-only.
if (kind === 'content') {
  const TPL = path.resolve(REPO, '..', '..', '_re', 'MODDEV', 'templates', 'content-pack');
  if (!fs.existsSync(TPL)) {
    console.error('template not found: ' + TPL);
    process.exit(1);
  }
  const rename = (name) => name
    .replace(/^ContentPack\.csproj$/, Pascal + '.csproj')
    .replace(/^ContentPackMod\.cs$/, Pascal + 'Mod.cs')
    .replace(/^content-pack\.suite\.json$/, folder + '.suite.json');
  // item ids must not collide with another installed mod (or with the monitor's 9000xx probes):
  // the template ships 900010..900014, every scaffold gets --base-id (default 910000).
  const baseId = Number(flag('base-id', '910000'));
  if (!Number.isInteger(baseId) || baseId < 900000) { console.error('--base-id must be an integer >= 900000'); process.exit(2); }
  const idMap = new Map([[900010, baseId], [900011, baseId + 1], [900012, baseId + 2], [900013, baseId + 3], [900014, baseId + 4]]);
  const remapIds = (text) => text.replace(/\b(90001[0-4])\b/g, (m) => String(idMap.get(Number(m))));
  const rebrand = (text) => remapIds(text)
    .split('Content Pack').join(Pascal)
    .split('com.mostima.contentpack').join(modId)
    .split('content-pack').join(folder)
    .split('ContentPack').join(Pascal)
    .split('Content_').join(Pascal + '_');

  const copyDir = (from, to) => {
    for (const e of fs.readdirSync(from, { withFileTypes: true })) {
      const srcPath = path.join(from, e.name);
      const dstPath = path.join(to, rename(e.name));
      if (e.isDirectory()) { fs.mkdirSync(dstPath, { recursive: true }); copyDir(srcPath, dstPath); continue; }
      if (/\.(png|jpg|jpeg)$/i.test(e.name)) { fs.copyFileSync(srcPath, dstPath); continue; }
      fs.writeFileSync(dstPath, rebrand(fs.readFileSync(srcPath, 'utf8')), 'utf8');
    }
  };
  fs.mkdirSync(dir, { recursive: true });
  copyDir(TPL, dir);
  console.log(`created work/${folder}/  (content pack: 2 potions, a charm, a weapon, a tablet)`);
  console.log(`  next:  node tools/build-mod.mjs ${folder} --deploy`);
  console.log(`         node tools/preflight.mjs work/${folder}`);
  process.exit(0);
}

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


console.log(`scaffolded work/${folder}`);
console.log(`  entry   ${Pascal}.${Pascal}Mod   modId ${modId}`);
console.log('next:');
console.log(`  next:  node tools/build-mod.mjs ${folder} --deploy`);
console.log(`  node tools/preflight.mjs work/${folder}`);
console.log('  node tools/new-mod.mjs is idempotent-safe: it refuses to overwrite an existing folder');
