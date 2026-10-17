import test from "node:test";
import assert from "node:assert/strict";
import {createHash,randomBytes} from "node:crypto";
import worker,{ControlPlane} from "../src/index.js";
import {streamDocument,validateChunk,downloadHeaders} from "../src/usb-documents.js";
const id="a".repeat(32), size=256*1024, name="강의 자료.pdf";
const bytes=randomBytes(size*2+17), hash=createHash("sha256").update(bytes).digest("hex");
const chunk=offset=>({id,name,size:bytes.length,offset,sha256:hash,data:bytes.subarray(offset,offset+size).toString("base64"),end:offset+size>=bytes.length});
function setup(){
  const writes=[],attachment={roomId:"m112",deviceId:"pc",installationId:"install"};
  const socket={deserializeAttachment:()=>attachment,send:message=>{
    const command=JSON.parse(message);const body=command.payload.payload.body;
    const value=command.payload.action==="usb.chunk"?chunk(body.offset):{enabled:true,items:[],total:0};
    queueMicrotask(()=>plane.webSocketMessage(socket,JSON.stringify({schemaVersion:1,type:"commandResult",
      requestId:command.requestId,payload:{commandId:command.requestId,ok:true,statusCode:200,body:JSON.stringify(value)}})));
  }};
  const device={...attachment,app:{usbDocumentsSupported:true,remoteUsbDocuments:true}};
  let online=true;
  const plane=new ControlPlane({storage:{get:async()=>device,put:async(...args)=>writes.push(args)},getWebSockets:room=>online&&room==="m112"?[socket]:[]},{});
  plane.role=async(email,room)=>email==="owner@example.com"&&room==="m112"?"manager":"none";
  return {plane,socket,device,writes,offline:()=>online=false};
}
const body={email:"owner@example.com",roomId:"m112",operation:"list"};
test("download is pull-driven, bounded and reconstructs the original SHA-256",async()=>{
  const offsets=[];let released=0;
  const response=await streamDocument(async offset=>{offsets.push(offset);return chunk(offset)},id,()=>released++);
  assert.deepEqual(offsets,[0]);
  const reader=response.body.getReader(),parts=[];
  while(true){const result=await reader.read();if(result.done)break;assert.ok(result.value.length<=size);parts.push(result.value);}
  assert.equal(createHash("sha256").update(Buffer.concat(parts)).digest("hex"),hash);
  assert.deepEqual(offsets,[0,size,size*2]);assert.equal(released,1);
  assert.match(response.headers.get("content-disposition"),/filename\*=UTF-8''/);
  assert.equal(response.headers.get("cache-control"),"no-store");
});
test("browser cancellation stops requesting chunks and releases the download slot",async()=>{
  let reads=0,released=0;
  const response=await streamDocument(async offset=>{reads++;return chunk(offset)},id,()=>released++);
  await response.body.cancel();
  assert.equal(reads,1);assert.equal(released,1);
});
test("malformed, reordered or changed chunks fail instead of completing",()=>{
  for(const value of [{...chunk(0),offset:1},{...chunk(0),data:"!"},{...chunk(0),end:true},{...chunk(0),data:"AAAA"},{...chunk(0),id:"b".repeat(32)}])
    assert.throws(()=>validateChunk(value,id,0));
  assert.throws(()=>validateChunk({...chunk(size),sha256:"b".repeat(64)},id,size,chunk(0)));
  assert.doesNotMatch(downloadHeaders("bad\r\n\\name.pdf",1)["content-disposition"],/[\r\n]/);
});
test("permission, offline and old capability checks run before sending document requests",async()=>{
  const s=setup();
  assert.equal((await s.plane.usbRequest({...body,roomId:"m111"})).status,403);
  s.device.app.remoteUsbDocuments=false;
  assert.equal((await s.plane.usbRequest(body)).status,409);
  s.device.app.remoteUsbDocuments=true;s.offline();
  assert.equal((await s.plane.usbRequest(body)).status,503);
  assert.equal(s.writes.length,0);
});
test("document list and bytes never enter persisted command history",async()=>{
  const s=setup();
  assert.equal((await s.plane.usbRequest(body)).status,200);
  const response=await s.plane.usbDownload({...body,id});
  const received=Buffer.from(await response.arrayBuffer());
  assert.deepEqual(received,bytes);assert.deepEqual(s.writes,[]);
  assert.equal(s.plane.usbPending.size,0);assert.equal(s.plane.usbDownloads.size,0);
});
test("only the socket that received a request may acknowledge it",async()=>{
  const s=setup();let handled=false;
  s.socket.send=message=>{
    const request=JSON.parse(message),reply=JSON.stringify({schemaVersion:1,type:"commandResult",requestId:request.requestId,
      payload:{commandId:request.requestId,ok:true,statusCode:200,body:'{"correct":true}'}});
    queueMicrotask(async()=>{
      await s.plane.webSocketMessage({deserializeAttachment:()=>({roomId:"m111"})},reply);
      assert.equal(handled,false);
      await s.plane.webSocketMessage(s.socket,reply);
    });
  };
  const result=await s.plane.usbRequest(body);handled=true;
  assert.deepEqual(await result.json(),{correct:true});
});
test("disconnect immediately fails pending request and clears state",async()=>{
  const s=setup();
  s.socket.send=()=>queueMicrotask(()=>s.plane.webSocketClose(s.socket));
  assert.equal((await s.plane.usbRequest(body)).status,503);
  assert.equal(s.plane.usbPending.size,0);
});
test("revocation during download stops next chunk and permits later downloads",async()=>{
  const s=setup();
  const response=await s.plane.usbDownload({...body,id});
  const reader=response.body.getReader();
  await reader.read();s.device.revokedAtUtc=new Date().toISOString();
  await assert.rejects(()=>reader.read());
  assert.equal(s.plane.usbDownloads.size,0);assert.equal(s.writes.length,0);
});
test("same room has only one active download; cancellation frees it",async()=>{
  const s=setup(),response=await s.plane.usbDownload({...body,id});
  assert.equal((await s.plane.usbDownload({...body,id})).status,429);
  await response.body.cancel();
  const second=await s.plane.usbDownload({...body,id});
  assert.equal(second.status,200);await second.body.cancel();
});
test("expired or absent session returns a bodyless 401 for both list and download",async()=>{
  const env={PUBLIC_BASE_URI:"https://admin.silentstream.win",DEVICE_BASE_URI:"https://agent.silentstream.win",GOOGLE_CLIENT_ID:"test",GOOGLE_CLIENT_SECRET:"test-secret"};
  for(const host of ["admin","m112"])for(const suffix of ["","/"+id+"/download"]){
    const response=await worker.fetch(new Request("https://"+host+".silentstream.win/api/control/rooms/m112/usb-documents"+suffix),env);
    assert.equal(response.status,401);assert.equal(await response.text(),"");
  }
});


test("room routes reject another room and never forward a user-selected file path",async()=>{
  const calls=[];
  const env={PUBLIC_BASE_URI:"https://admin.silentstream.win",DEVICE_BASE_URI:"https://agent.silentstream.win",
    GOOGLE_CLIENT_ID:"test",GOOGLE_CLIENT_SECRET:"secret",ENABLE_DEVELOPMENT_IDENTITY:"true",
    CONTROL:{idFromName:()=>"global",get:()=>({fetch:async request=>{
      const path=new URL(request.url).pathname,body=await request.json();calls.push({path,body});
      if(path==="/authorize-room")return Response.json({allowed:true});
      if(path==="/usb-request")return Response.json({items:[],total:0});
      if(path==="/usb-download")return new Response("file");
      return new Response(null,{status:404});
    }})}};
  const headers={"x-silentstream-dev-email":"manager@example.com",origin:"https://m112.silentstream.win"};
  const cross=await worker.fetch(new Request("https://m112.silentstream.win/api/control/rooms/m113/usb-documents",{headers}),env);
  assert.equal(cross.status,403);assert.equal(calls.some(c=>c.path==="/usb-request"),false);
  const own=await worker.fetch(new Request("https://m112.silentstream.win/api/control/rooms/m112/usb-documents",{headers}),env);
  assert.equal(own.status,200);
  const changed=await worker.fetch(new Request("https://m112.silentstream.win/api/control/rooms/m112/usb-documents",
    {method:"POST",headers:{...headers,"content-type":"application/json"},body:JSON.stringify({operation:"settings",enabled:true,path:"C:/private"})}),env);
  assert.equal(changed.status,200);
  assert.deepEqual(calls.at(-1).body.payload,{enabled:true});
  const download=await worker.fetch(new Request("https://m112.silentstream.win/api/control/rooms/m112/usb-documents/"+id+"/download",{headers}),env);
  assert.equal(download.status,200);assert.equal(await download.text(),"file");
});

test("disconnect while browser is idle aborts the stream and releases its room slot",async()=>{
  const s=setup(),response=await s.plane.usbDownload({...body,id});
  await s.plane.webSocketClose(s.socket);
  await assert.rejects(()=>response.arrayBuffer());
  assert.equal(s.plane.usbDownloads.size,0);
});
