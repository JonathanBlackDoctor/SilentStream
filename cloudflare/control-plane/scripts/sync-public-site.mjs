import { mkdir, copyFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
// docs remains the source of truth; only these public files enter the Worker assets.
for (const name of ["index.html", "guide.html", "assets/site.css", "assets/site.js"]) {
  const target = path.join(project, "public/site", name);
  await mkdir(path.dirname(target), { recursive: true });
  await copyFile(path.join(project, "../../docs", name), target);
}
