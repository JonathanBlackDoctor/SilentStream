import test from "node:test";
import assert from "node:assert/strict";
import {createHash} from "node:crypto";
import worker,{ControlPlane} from "../src/index.js";
import {CentralDocumentDrive} from "../src/central-document-drive.js";
test("native fetch is called without a class receiver in the Worker runtime",async()=>{
  const original=globalThis.fetch;
  try{
    globalThis.fetch=function(){assert.equal(this,undefined);return Promise.resolve(Response.json({ok:true}));};
    const client=new CentralDocumentDrive({state:{storage:{}},env:{}});
    assert.equal((await client.send("https://www.googleapis.com/drive/v3/about")).status,200);
  }finally{globalThis.fetch=original;}
});

test("expired central authorization is persisted and is not automatically refreshed again",async()=>{
  const f=await fixture();let refreshes=0;
  f.drive.send=async()=>{refreshes++;return new Response(null,{status:400});};
  await assert.rejects(()=>f.drive.token());await assert.rejects(()=>f.drive.token());
  assert.equal(refreshes,1);assert.ok(f.data.get("centralDrive").error);
});
test("a scan failure is persisted until explicit retry rather than polling the same failure",async()=>{
  const f=await fixture();let scans=0;
  f.plane.usbRequest=async()=>{scans++;return new Response(null,{status:503});};
  await f.drive.run();await f.drive.run();assert.equal(scans,1);
  await f.drive.retry();await f.drive.run();assert.equal(scans,2);
});
test("upload pagination retains work beyond the first hundred completed records",async()=>{
  const f=await fixture();for(let n=0;n<101;n++)f.data.set("centralUpload:"+String(n).padStart(3,"0"),{status:"uploaded"});
  await f.drive.run();assert.ok(f.data.get("centralUploadCursor"));
  await f.drive.run();assert.equal(f.data.get("centralUploadCursor"),"");
});
test("PC acknowledgements cannot overwrite centrally verified Google permissions",async()=>{
  const f=await fixture();
  await f.plane.acknowledgeDrive("m112",{revision:f.p.revision,appliedRevision:"",folderId:f.p.folderId,readerEmail:reader,error:"fake local error"},{app:{documentDriveSupported:true}});
  assert.equal(f.data.get("driveFolder:m112").verified,true);
});
const scope="https://www.googleapis.com/auth/drive",C=256*1024;
const owner="storage@example.test",reader="reader@example.test",admin="admin@example.test";
async function fixture(size=C*9+17){
  const data=new Map(),log=[],content=Buffer.alloc(size,7),sha=createHash("sha256").update(content).digest("hex");
  const p={roomId:"m112",account:owner,readerEmail:reader,folderId:"folder-id-12345",revision:"a".repeat(32),central:true,verified:true,requestedBy:admin};
  let alarm=null,online=true;
  const binding={roomId:"m112",deviceId:"pc",installationId:"install"};
  const storage={get:async k=>structuredClone(data.get(k)),put:async(k,v)=>data.set(k,structuredClone(v)),delete:async k=>data.delete(k),
    list:async({prefix,startAfter="",limit=100000})=>new Map([...data].filter(([k])=>k.startsWith(prefix)&&k>startAfter).sort(([a],[b])=>a.localeCompare(b)).slice(0,limit).map(([k,v])=>[k,structuredClone(v)])),
    transaction:async f=>f(storage),getAlarm:async()=>alarm,setAlarm:async n=>alarm=n};
  const plane=new ControlPlane({storage,getWebSockets:()=>online?[{deserializeAttachment:()=>binding}]:[]},{
    DOCUMENT_DRIVE_TOKEN_KEY:Buffer.alloc(32,3).toString("base64"),GOOGLE_CLIENT_ID:"client",GOOGLE_CLIENT_SECRET:"secret",PUBLIC_BASE_URI:"https://admin.silentstream.win"});
  plane.role=async(e,room)=>e===admin?"admin":e===reader&&room==="m112"?"manager":"none";
  plane.usbRequest=async body=>{
    if(body.operation==="list")return Response.json({enabled:true,total:1,items:[{id:"a".repeat(32),name:"private-course.pdf",date:"2026-10-20",status:"ready",sha256:sha,size}]});
    const offset=body.payload.offset;
    return Response.json({id:"a".repeat(32),name:"private-course.pdf",size,sha256:sha,offset,data:content.subarray(offset,offset+C).toString("base64"),end:offset+C>=size});
  };
  const remote={received:0,ids:0,posts:0,mutations:[],account:owner,permissions:[
    {id:"owner",type:"user",role:"owner",emailAddress:owner},{id:"reader",type:"user",role:"reader",emailAddress:reader}]};
  const send=async(url,options={})=>{
    const u=new URL(url),method=options.method||"GET";log.push({url,method});
    if(u.hostname==="oauth2.googleapis.com"){
      assert.equal(options.redirect,"manual");
      return Response.json({access_token:"access-secret",refresh_token:"refresh-secret",scope,expires_in:3600});
    }
    if(u.pathname.endsWith("/about"))return Response.json({user:{emailAddress:remote.account}});
    if(u.pathname.includes("/permissions")){
      if(method==="GET")return Response.json({permissions:remote.permissions});
      remote.mutations.push({url,method,body:options.body});
      if(method==="DELETE"){remote.permissions=remote.permissions.filter(v=>v.id!==u.pathname.split("/").at(-1));return new Response(null,{status:204});}
      if(method==="PATCH"){remote.permissions.find(v=>v.id===u.pathname.split("/").at(-1)).role="reader";return Response.json({});}
      remote.permissions.push({id:"granted",...JSON.parse(options.body)});return Response.json({id:"granted"});
    }
    if(u.pathname.endsWith("/generateIds")){remote.ids++;return Response.json({ids:["remote-file-id-12345"]});}
    if(u.pathname==="/upload/drive/v3/files"){
      if(method==="POST"){remote.posts++;return new Response(null,{status:200,headers:{location:"https://www.googleapis.com/upload/drive/v3/files?upload_id=session-secret"}});}
      const range=options.headers["content-range"];
      if(range.startsWith("bytes */"))return remote.received===size?Response.json({id:"remote-file-id-12345"}):
        new Response(null,{status:308,headers:remote.received?{range:"bytes=0-"+(remote.received-1)}:{}});
      const m=range.match(/^bytes (\d+)-(\d+)\/(\d+)$/);assert.ok(m);
      assert.equal(Number(m[1]),remote.received);assert.deepEqual(Buffer.from(options.body),content.subarray(Number(m[1]),Number(m[2])+1));
      remote.received=Number(m[2])+1;
      return remote.received===size?Response.json({id:"remote-file-id-12345"}):new Response(null,{status:308,headers:{range:"bytes=0-"+(remote.received-1)}});
    }
    if(u.pathname.endsWith("/"+p.folderId))return Response.json({id:p.folderId,mimeType:"application/vnd.google-apps.folder",trashed:false,owners:[{emailAddress:owner}],capabilities:{canAddChildren:true}});
    if(u.pathname.endsWith("/remote-file-id-12345"))return remote.received===size?Response.json({id:"remote-file-id-12345",size:String(size),sha256Checksum:sha,parents:[p.folderId],trashed:false,appProperties:{silentStreamSha256:sha}}):new Response(null,{status:404});
    throw Error("unexpected "+url);
  };
  const drive=new CentralDocumentDrive(plane,send);plane.centralDrive=drive;
  data.set("driveFolder:m112",p);data.set("device:pc",{...binding,app:{centralDocumentDrive:true}});
  data.set("centralDrive",{account:owner,credentials:await drive.seal({refreshToken:"refresh-secret"},"centralDrive"),error:""});
  return {plane,drive,storage,data,p,remote,log,send,offline:()=>online=false,sha,size};
}
test("central credentials are encrypted and authenticated to their storage record",async()=>{
  const f=await fixture();const sealed=await f.drive.seal({token:"private-refresh"},"record-a");
  assert.doesNotMatch(JSON.stringify(sealed),/private-refresh/);
  assert.deepEqual(await f.drive.open(sealed,"record-a"),{token:"private-refresh"});
  await assert.rejects(()=>f.drive.open(sealed,"record-b"));
  sealed.data=sealed.data.slice(0,-4)+"AAAA";await assert.rejects(()=>f.drive.open(sealed,"record-a"));
});
test("only an admin starts one offline consent flow; expected account, PKCE and existing callback are bound",async()=>{
  const f=await fixture();await assert.rejects(()=>f.drive.begin(reader));
  const value=await f.drive.begin(admin),url=new URL(value.url);
  assert.equal(url.origin,"https://accounts.google.com");assert.equal(url.searchParams.get("access_type"),"offline");
  assert.equal(url.searchParams.get("login_hint"),owner);assert.equal(url.searchParams.get("code_challenge_method"),"S256");
  assert.equal(url.searchParams.get("redirect_uri"),"https://admin.silentstream.win/auth/callback");
  assert.match(url.searchParams.get("scope"),/auth\/drive/);assert.equal(value.state,url.searchParams.get("state"));
});
test("callback rejects cookie mismatch, replay, expired requests and the wrong Google account",async()=>{
  const f=await fixture(),start=await f.drive.begin(admin);
  await assert.rejects(()=>f.drive.callback({state:start.state,cookieState:"other",code:"code"}));
  f.remote.account="personal@example.test";
  await assert.rejects(()=>f.drive.callback({state:start.state,cookieState:start.state,code:"code"}));
  f.remote.account=owner;
  await assert.rejects(()=>f.drive.callback({state:start.state,cookieState:start.state,code:"code"}));
  const second=await f.drive.begin(admin);
  for(const [key,value] of f.data)if(key.startsWith("centralDriveOAuth:"))value.expires=0;
  await assert.rejects(()=>f.drive.callback({state:second.state,cookieState:second.state,code:"code"}));
});
test("one successful callback enables all mapped devices without changing human sessions or distributing tokens",async()=>{
  const f=await fixture();f.data.set("driveFolder:m111",{...f.p,roomId:"m111",folderId:"other-folder-12345",readerEmail:""});
  f.data.set("session:human",{email:admin});
  const start=await f.drive.begin(admin);await f.drive.callback({state:start.state,cookieState:start.state,code:"code"});
  assert.equal(f.data.get("session:human").email,admin);
  for(const room of ["m111","m112"]){const p=f.data.get("driveFolder:"+room);assert.equal(p.central,true);assert.equal(p.verified,false);assert.notEqual(p.revision,f.p.revision);}
  assert.doesNotMatch(JSON.stringify([...f.data]),/refresh-secret|access-secret/);
  const status=await f.drive.status();assert.equal(status.connected,true);assert.doesNotMatch(JSON.stringify(status),/token|credentials/i);
});
test("central ACL provisioning removes public access and grants only the assigned reader without email",async()=>{
  const f=await fixture();f.remote.permissions=[f.remote.permissions[0],{id:"public",type:"anyone",role:"reader"}];
  await f.drive.restrict(f.p);
  assert.equal(f.remote.mutations.length,2);assert.equal(f.remote.mutations[0].method,"DELETE");
  assert.match(f.remote.mutations[1].url,/sendNotificationEmail=false/);
  assert.deepEqual(JSON.parse(f.remote.mutations[1].body),{type:"user",role:"reader",emailAddress:reader});
});
test("inherited external access fails closed and is attempted once per policy until explicit retry",async()=>{
  const f=await fixture();f.remote.permissions.push({id:"public",type:"anyone",role:"reader",permissionDetails:[{inherited:true}]});
  f.p.verified=false;f.data.set("driveFolder:m112",f.p);
  await f.drive.applyPolicy("driveFolder:m112",f.p);
  const requests=f.log.length;
  await f.drive.applyPolicy("driveFolder:m112",f.data.get("driveFolder:m112"));
  assert.equal(f.log.length,requests);assert.equal(f.remote.mutations.length,0);assert.equal(f.data.get("driveFolder:m112").verified,false);
  await f.drive.retry();assert.equal(f.data.get("driveFolder:m112").attempted,"");
});
test("bounded transfer checkpoints resume after a new worker and preserve one remote ID",async()=>{
  const f=await fixture();await f.drive.discover(f.p);
  const [key]=[...f.data.keys()].filter(k=>k.startsWith("centralUpload:"));
  await f.drive.transfer(key,f.data.get(key));
  assert.equal(f.remote.received,C*8);assert.equal(f.data.get(key).status,"uploading");
  const next=new CentralDocumentDrive(f.plane,f.send);
  await next.transfer(key,f.data.get(key));
  assert.equal(f.data.get(key).status,"uploaded");assert.equal(f.remote.received,f.size);
  assert.equal(f.remote.ids,1);assert.equal(f.remote.posts,1);
  assert.doesNotMatch(JSON.stringify(f.data.get(key)),/session-secret|private-course|access-secret/);
  await f.drive.discover(f.p);assert.equal([...f.data.keys()].filter(k=>k.startsWith("centralUpload:")).length,1);
});
test("unknown final checksum never marks a file complete",async()=>{
  const f=await fixture(7);await f.drive.discover(f.p);
  const [key]=[...f.data.keys()].filter(k=>k.startsWith("centralUpload:"));
  const original=f.drive.send;
  f.drive.send=async(url,options)=>{
    const r=await original(url,options);
    if(url.includes("/files/remote-file-id")&&r.ok){const data=await r.json();delete data.sha256Checksum;return Response.json(data);}
    return r;
  };
  await assert.rejects(()=>f.drive.transfer(key,f.data.get(key)));
  assert.notEqual(f.data.get(key).status,"uploaded");
});
test("unassigned devices archive privately for their owner without exposing a reader link",async()=>{
  const f=await fixture(7);f.p.readerEmail="";f.p.verified=false;f.data.set("driveFolder:m112",f.p);
  await f.drive.run();
  assert.equal(f.remote.received,7);assert.deepEqual(f.remote.permissions.map(p=>p.role),["owner"]);
  const view=await(await f.plane.driveFolder({email:reader,roomId:"m112"})).json();
  assert.equal(view.url,"");assert.equal(view.readerEmail,"");assert.equal(view.verified,true);
});
test("offline devices, old apps and existing failed files do not create more work",async()=>{
  const f=await fixture();f.offline();await f.drive.discover(f.p);assert.equal(f.remote.ids,0);
  const g=await fixture();g.data.get("device:pc").app.centralDocumentDrive=false;await g.drive.discover(g.p);
  assert.equal([...g.data.keys()].some(k=>k.startsWith("centralUpload:")),false);
  const h=await fixture();await h.drive.discover(h.p);const key=[...h.data.keys()].find(k=>k.startsWith("centralUpload:"));
  h.data.get(key).status="failed";await h.drive.discover(h.p);assert.equal(h.data.get(key).status,"failed");
});
test("an ACL or device binding change stops an in-progress source before another chunk is sent",async()=>{
  const f=await fixture();await f.drive.discover(f.p);const key=[...f.data.keys()].find(k=>k.startsWith("centralUpload:"));
  f.data.get(key).deviceId="other-device";await assert.rejects(()=>f.drive.transfer(key,f.data.get(key)));assert.equal(f.remote.received,0);
  f.data.get(key).deviceId="pc";f.data.get("driveFolder:m112").revision="b".repeat(32);
  await assert.rejects(()=>f.drive.transfer(key,f.data.get(key)));assert.equal(f.remote.received,0);
});
test("no caller can send a bearer token or document to a foreign upload location",async()=>{
  const f=await fixture();const n=f.log.length;
  await assert.rejects(()=>f.drive.request("https://evil.example/upload",{body:"document"}));assert.equal(f.log.length,n);
});
test("central status APIs reject managers and OAuth authorization responses never expose tokens",async()=>{
  const f=await fixture();
  assert.equal((await f.plane.centralDriveRequest({email:reader,operation:"status"})).status,403);
  const r=await f.plane.centralDriveRequest({email:admin,operation:"status"});assert.equal(r.status,200);
  assert.doesNotMatch(await r.text(),/refresh|access_token|credentials/);
});
test("web OAuth connect is same-origin and central-host only; a host cookie binds the callback",async()=>{
  const calls=[],env={ENABLE_DEVELOPMENT_IDENTITY:"true",PUBLIC_BASE_URI:"https://admin.silentstream.win",DEVICE_BASE_URI:"https://agent.silentstream.win",
    GOOGLE_CLIENT_ID:"client",GOOGLE_CLIENT_SECRET:"secret",CONTROL:{idFromName:()=>1,get:()=>({fetch:async r=>{
      const body=await r.json();calls.push({path:new URL(r.url).pathname,body});
      return Response.json(new URL(r.url).pathname==="/authorize-room"?{allowed:true}:{url:"https://accounts.google.com/o/oauth2/v2/auth?state=drive-state",state:"drive-state"});
    }})}};
  const headers={"x-silentstream-dev-email":admin,origin:"https://admin.silentstream.win"};
  const foreign=await worker.fetch(new Request("https://admin.silentstream.win/api/control/document-drive/connect",{method:"POST",headers:{...headers,origin:"https://evil.example"}}),env);
  assert.equal(foreign.status,403);assert.equal(calls.length,0);
  const r=await worker.fetch(new Request("https://admin.silentstream.win/api/control/document-drive/connect",{method:"POST",headers}),env);
  assert.equal(r.status,200);assert.match(r.headers.get("set-cookie"),/__Host-SilentStreamDriveState=drive-state; Path=\/; Secure; HttpOnly; SameSite=Lax/);
  assert.equal((await r.json()).state,undefined);
  const room=await worker.fetch(new Request("https://m112.silentstream.win/api/control/document-drive/connect",{method:"POST",headers:{...headers,origin:"https://m112.silentstream.win"}}),env);
  assert.equal(room.status,404);
});
