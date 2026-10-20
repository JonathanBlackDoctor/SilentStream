import test from "node:test";
import {readFile} from "node:fs/promises";
import {Script} from "node:vm";

test("shared USB renderer parses before being embedded in desktop and room controllers",async()=>{
  new Script(await readFile(new URL("../public/room/usb-documents-view.js",import.meta.url),"utf8"));
});
