// Renders the HUD icon set to PNG (white strokes on transparent, 192 px) for Unity.
// Icons come from Lucide (ISC, https://lucide.dev); "crouch" is drawn here in the same style.
// Usage: node Tools/UiIcons/build-icons.mjs <path-to-a-node_modules-with-lucide-react-and-sharp>
//   e.g. node Tools/UiIcons/build-icons.mjs ../FE/node_modules
import { createRequire } from "node:module";
import { existsSync, mkdirSync, readdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const modules = resolve(process.argv[2] ?? join(here, "../../../FE/node_modules"));
// pnpm keeps transitive packages (sharp comes with Next.js) under .pnpm.
const pnpmSharp = existsSync(join(modules, ".pnpm"))
  ? readdirSync(join(modules, ".pnpm")).find((d) => d.startsWith("sharp@"))
  : undefined;
const require = createRequire(join(pnpmSharp ? join(modules, ".pnpm", pnpmSharp, "node_modules") : modules, "noop.js"));
const sharp = require("sharp");
const out = resolve(here, "../../Assets/_Fire3D/Resources/UI/Icons");
mkdirSync(out, { recursive: true });

// Unity name → Lucide icon.
const icons = {
  touch: "hand",
  open: "door-open",
  close: "door-closed",
  pick_up: "grab",
  belongings: "briefcase-business",
  wet_item: "droplets",
  cover_nose: "wind",
  press: "bell-ring",
  pull_pin: "pin-off",
  spray: "spray-can",
  extinguisher: "fire-extinguisher",
  elevator: "arrow-up-down",
  call: "megaphone",
  lead: "hand-helping",
  stand: "person-standing",
  pause: "pause",
  play: "play",
  heart: "heart",
  stamina: "zap",
  smoke: "cloud-fog",
  heat: "thermometer",
  timer: "timer",
  objective: "target",
  fire: "flame",
  warning: "triangle-alert",
  success: "shield-check",
  info: "info",
  towel: "shirt",
  exit: "log-out",
  hand_empty: "hand",
  sprint: "chevrons-up",
  view_first: "eye",
  view_third: "user-round",
};

const custom = {
  // Crouching figure, Lucide stroke conventions (24 grid, 2 px, round joins).
  crouch: [
    ["circle", { cx: "13.5", cy: "6", r: "2" }],
    ["path", { d: "M12.5 9.5 9 13.5h5l-.5 6.5" }],
    ["path", { d: "M9 13.5 7.5 20" }],
    ["path", { d: "M12 10.5l3.5 2" }],
    ["path", { d: "M4 21h16" }],
  ],};

const attrs = (o) => Object.entries(o).filter(([k]) => k !== "key").map(([k, v]) => `${k}="${v}"`).join(" ");
const svg = (nodes, stroke = 2.1) =>
  `<svg xmlns="http://www.w3.org/2000/svg" width="192" height="192" viewBox="0 0 24 24" fill="none" stroke="#ffffff" ` +
  `stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round">` +
  nodes.map(([tag, a]) => `<${tag} ${attrs(a)}/>`).join("") + `</svg>`;

const iconFile = (name) => pathToFileURL(join(modules, "lucide-react/dist/esm/icons", `${name}.js`)).href;

for (const [unityName, lucide] of Object.entries(icons)) {
  const mod = await import(iconFile(lucide));
  await sharp(Buffer.from(svg(mod.__iconNode))).png().toFile(join(out, `${unityName}.png`));
}
for (const [unityName, nodes] of Object.entries(custom))
  await sharp(Buffer.from(svg(nodes))).png().toFile(join(out, `${unityName}.png`));

writeFileSync(join(out, "LICENSE-lucide.txt"),
  "Icons from Lucide (https://lucide.dev), ISC License.\n" +
  "Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT).\n" +
  "All other copyright (c) for Lucide are held by Lucide Contributors 2022.\n\n" +
  "Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted,\n" +
  "provided that the above copyright notice and this permission notice appear in all copies.\n\n" +
  "THE SOFTWARE IS PROVIDED \"AS IS\" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE.\n");
console.log(`wrote ${Object.keys(icons).length + Object.keys(custom).length} icons to ${out}`);
