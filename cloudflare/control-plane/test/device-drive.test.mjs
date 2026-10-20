import test from "node:test";
import assert from "node:assert/strict";
import {ControlPlane} from "../src/index.js";
function setup(){
  const data=new Map([["member:admin@example.test",{email:"admin@example.test",role:"admin",rooms:[]}],
    ["member:reader@example.test",{email:"reader@example.test",role:"manager",rooms:["m112"]}],
    ["member:other@example.test",{email:"other@example.test",role:"admin",rooms:[]}]]);
  const storage={get:async k=>data.get(k),put:async(k,v)=>data.set(k,v),delete:async k=>data.delete(k),
    list:async({prefix})=>new Map([...data].filter(([k])=>k.startsWith(prefix))),transaction:async f=>f(storage)};
  const plane=new ControlPlane({storage,getWebSockets:()=>[]},{});
  const body={email:"admin@example.test",roomId:"m112",policy:{account:"storage@example.test",folderId:"folder-m112-12345",readerEmail:"reader@example.test"}};
  return {plane,data,body};
}
test("Drive assignment preserves existing controller role and is pending until authenticated PC verification",async()=>{
  const {plane,data,body}=setup();
  const first=await (await plane.driveFolder(body)).json();
  assert.equal(first.verified,false);assert.equal(first.url,"");
  assert.equal(data.get("member:reader@example.test").role,"manager");
  assert.equal(data.get("member:admin@example.test").role,"admin");
  assert.equal((await plane.driveFolder({...body,email:"reader@example.test"})).status,403);
  assert.equal((await plane.driveFolder({...body,policy:{...body.policy,readerEmail:"stranger@example.test"}})).status,409);
});
test("only assigned Google reader gets a link; another admin and another room cannot read it",async()=>{
  const {plane,data,body}=setup();await plane.driveFolder(body);
  const p=data.get("driveFolder:m112");
  await plane.acknowledgeDrive("m112",{revision:p.revision,appliedRevision:p.revision,folderId:p.folderId,readerEmail:p.readerEmail,error:""},{app:{documentDriveSupported:true}});
  const reader=await(await plane.driveFolder({email:"reader@example.test",roomId:"m112"})).json();
  assert.match(reader.url,/drive.google.com\/drive\/folders\/folder-m112-12345\?authuser=reader%40example.test/);
  assert.equal((await(await plane.driveFolder({email:"other@example.test",roomId:"m112"})).json()).url,"");
  assert.equal((await plane.driveFolder({email:"reader@example.test",roomId:"m111"})).status,403);
});
test("wrong revision, folder, reader or old app cannot acknowledge Google access",async()=>{
  const {plane,data,body}=setup();await plane.driveFolder(body);const p=data.get("driveFolder:m112");
  const status={revision:p.revision,appliedRevision:p.revision,folderId:p.folderId,readerEmail:p.readerEmail,error:""};
  for(const change of [{revision:"wrong"},{folderId:"other-folder"},{readerEmail:"other@example.test"}])
    await plane.acknowledgeDrive("m112",{...status,...change},{app:{documentDriveSupported:true}});
  await plane.acknowledgeDrive("m112",status,{app:{}});
  assert.equal(data.get("driveFolder:m112").verified,false);
});
test("clearing a reader remains pending and prevents membership deletion until ACL revocation is verified",async()=>{
  const {plane,data,body}=setup();await plane.driveFolder(body);
  let p=data.get("driveFolder:m112");
  await plane.acknowledgeDrive("m112",{revision:p.revision,appliedRevision:p.revision,folderId:p.folderId,readerEmail:p.readerEmail,error:""},{app:{documentDriveSupported:true}});
  assert.equal((await plane.deleteMember({email:body.email,target:p.readerEmail})).status,409);
  await plane.driveFolder({...body,policy:{...body.policy,readerEmail:""}});
  p=data.get("driveFolder:m112");assert.equal(p.effectiveReaderEmail,"reader@example.test");
  assert.equal((await plane.deleteMember({email:body.email,target:"reader@example.test"})).status,409);
  await plane.acknowledgeDrive("m112",{revision:p.revision,appliedRevision:p.revision,folderId:p.folderId,readerEmail:"",error:""},{app:{documentDriveSupported:true}});
  assert.equal((await plane.deleteMember({email:body.email,target:"reader@example.test"})).status,200);
});
test("a folder cannot be reused for another device or silently replaced",async()=>{
  const {plane,body}=setup();await plane.driveFolder(body);
  assert.equal((await plane.driveFolder({...body,roomId:"m111",policy:{...body.policy,readerEmail:""}})).status,409);
  assert.equal((await plane.driveFolder({...body,policy:{...body.policy,folderId:"other-folder-12345"}})).status,409);
});
test("server strips forged policy and sends only its own assignment over existing USB bridge",async()=>{
  const {plane,data,body}=setup();await plane.driveFolder(body);let sent;
  const attachment={roomId:"m112",deviceId:"pc",installationId:"installation"};
  data.set("device:pc",{...attachment,app:{usbDocumentsSupported:true,remoteUsbDocuments:true,documentDriveSupported:true}});
  const socket={deserializeAttachment:()=>attachment,send:message=>{
    const c=JSON.parse(message);sent=c.payload.payload.body;
    queueMicrotask(()=>plane.webSocketMessage(socket,JSON.stringify({schemaVersion:1,type:"commandResult",requestId:c.requestId,
      payload:{commandId:c.requestId,ok:true,statusCode:200,body:JSON.stringify({items:[]})}})));
  }};
  plane.state.getWebSockets=()=>[socket];
  await plane.usbRequest({email:body.email,roomId:"m112",operation:"list",payload:{drivePolicy:{folderId:"attacker"},date:"2026-10-20"}});
  assert.equal(sent.drivePolicy.folderId,body.policy.folderId);assert.equal(sent.drivePolicy.readerEmail,body.policy.readerEmail);
  assert.equal(sent.date,"2026-10-20");
});
