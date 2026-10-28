import test from "node:test";
import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import vm from "node:vm";
import {ControlPlane,normalizeAppStatus} from "../src/index.js";

const source=await readFile(new URL("../public/room/app.js",import.meta.url),"utf8");
const selected=source.slice(source.indexOf("function uploadRetryIds("),source.indexOf("function uploadPanel("));
function setup(){
  const calls=[];
  const context=vm.createContext({controllerFresh:v=>v.online===true,act:async(...a)=>calls.push(a),
    koreaToday:()=>"2026-10-28",uploadDate:"",device:null});
  vm.runInContext(selected,context);
  return {context,calls};
}
const device={online:true,agentVersion:"0.4.22",app:{uploadRetryControl:true,queue:{items:[
  {id:"retry",date:"2026-10-28",status:"failed"},
  {id:"complete",date:"2026-10-28",status:"completed",videoId:"already"},
  {id:"active",date:"2026-10-28",status:"uploading"},
  {id:"previous",date:"2026-10-27",status:"failed"},
  {id:"known-video",date:"2026-10-28",status:"failed",videoId:"exists"}
]}}};
test("retries the selected day's failed jobs only and recomputes the selection at click time",async()=>{
  const {context,calls}=setup();
  context.device=structuredClone(device);
  await context.retryUploads();
  assert.equal(calls[0][0],"uploads.retry");
  assert.deepEqual(Array.from(calls[0][2].ids),["retry"]);
  context.device.app.queue.items[0].status="completed";
  await context.retryUploads();
  assert.equal(calls.length,1);
});
test("retry stays unavailable on offline devices, old helpers and apps without support",()=>{
  const {context}=setup();
  for(const value of [{...device,online:false},{...device,agentVersion:"0.4.11"},
    {...device,agentVersion:"garbage"},{...device,app:{...device.app,uploadRetryControl:false}}])
    assert.equal(context.uploadRetryIds(value,"2026-10-28").length,0);
  assert.equal(normalizeAppStatus({uploadRetryControl:true}).uploadRetryControl,true);
});
test("central retry checks room permissions before dispatching a command",async()=>{
  const storage={async get(key){return key==="ownerEmail"?"owner@example.com":null}};
  const plane=new ControlPlane({storage}, {});
  const response=await plane.command({email:"outsider@example.com",roomId:"m112",
    action:"uploads.retry",body:{ids:["retry"]}});
  assert.equal(response.status,403);
});
