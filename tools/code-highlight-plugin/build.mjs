import { build } from "esbuild";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { deflateRawSync, crc32 } from "node:zlib";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..", "..");
const outputDirectory = path.join(repoRoot, "artifacts", "plugins");
const scriptPath = path.join(outputDirectory, "code-highlight.js");
const packagePath = path.join(outputDirectory, "code-highlight.lspkg");
const licenseFileName = "LICENSE.txt";

// The bundle is minified, so keep the upstream MIT notice both as a banner and
// as a stand-alone file inside the .lspkg manifest (the package loader rejects
// files that are not declared in plugin.json).
const shikiLicense = await readFile(path.join(here, "node_modules", "shiki", "LICENSE"));
const noticeBanner = [
  "code-highlight plugin for Remote CLI.",
  "Bundles shiki 4.5.0 (https://github.com/shikijs/shiki), MIT licensed.",
  "Copyright (c) 2021 Pine Wu; 2023 Anthony Fu.",
  "@shikijs/core, engine-javascript, langs, themes, primitive 4.5.0: same copyright holders.",
  "oniguruma-to-es 4.3.6, oniguruma-parser 0.12.2, regex 6.1.0,",
  "regex-recursion 6.0.2, regex-utilities 2.3.0: Copyright (c) Steven Levithan, MIT licensed.",
  "Full MIT text: LICENSE.txt in this package and ThirdPartyNotices/shiki-MIT.txt.",
  "Per-component notice: ThirdPartyNotices/shiki-bundle-NOTICE.txt."
].join("\n");

function sha256(buffer) {
  return createHash("sha256").update(buffer).digest("hex").toUpperCase();
}

function createZip(files) {
  const localParts = [];
  const centralParts = [];
  let offset = 0;
  for (const file of files) {
    const name = Buffer.from(file.name, "utf8");
    const data = Buffer.from(file.data);
    const compressed = deflateRawSync(data, { level: 9 });
    const crc = crc32(data) >>> 0;

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(8, 8);
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(0x21, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(compressed.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    localParts.push(local, name, compressed);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt16LE(0, 12);
    central.writeUInt16LE(0x21, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(compressed.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt16LE(0, 30);
    central.writeUInt16LE(0, 32);
    central.writeUInt16LE(0, 34);
    central.writeUInt16LE(0, 36);
    central.writeUInt32LE(0, 38);
    central.writeUInt32LE(offset, 42);
    centralParts.push(central, name);

    offset += local.length + name.length + compressed.length;
  }

  const centralDirectory = Buffer.concat(centralParts);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(0, 4);
  end.writeUInt16LE(0, 6);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(centralDirectory.length, 12);
  end.writeUInt32LE(offset, 16);
  end.writeUInt16LE(0, 20);
  return Buffer.concat([...localParts, centralDirectory, end]);
}

await mkdir(outputDirectory, { recursive: true });
await build({
  entryPoints: [path.join(here, "src", "entry.js")],
  bundle: true,
  minify: true,
  format: "iife",
  target: ["chrome110"],
  legalComments: "eof",
  banner: { js: "/*! " + noticeBanner.replace(/\n/g, "\n * ") + "\n */" },
  outfile: scriptPath
});

const script = await readFile(scriptPath);
const manifest = {
  id: "code-highlight",
  name: "代码高亮",
  version: "1.0.0",
  kind: "ui-script",
  entry: "code-highlight.js",
  files: {
    "code-highlight.js": sha256(script),
    [licenseFileName]: sha256(shikiLicense)
  }
};
const pluginJson = Buffer.from(JSON.stringify(manifest, null, 2) + "\n", "utf8");
const packageBytes = createZip([
  { name: "plugin.json", data: pluginJson },
  { name: "code-highlight.js", data: script },
  { name: licenseFileName, data: shikiLicense }
]);
await writeFile(packagePath, packageBytes);

console.log("script " + script.length + " bytes");
console.log("package " + packageBytes.length + " bytes");
console.log("sha256 " + sha256(packageBytes));
console.log(packagePath);
