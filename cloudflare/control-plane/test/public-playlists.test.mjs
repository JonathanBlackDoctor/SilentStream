import test from 'node:test';
import assert from 'node:assert/strict';
import worker, {ControlPlane, recentPublicPlaylists} from '../src/index.js';
const row=(date,extra={})=>({date,status:'ready',playlistId:'PL_'+date.replaceAll('-',''),...extra});
const device={roomId:'m112',deviceId:'device-1',lastSeenAtUtc:'2026-10-27T02:11:00Z',secretHash:'never-share',machineName:'never-share',playlists:{room:'m112',items:['2026-10-28','2026-10-27','2026-10-26','2026-10-23','2026-10-22','2026-10-21','2026-10-20'].map(x=>row(x))}};
function setup(enabled=true, selected=device) {
  const values=new Map([['roomDevices:m112',['device-1']],['device:device-1',selected]]);
  const env={PUBLIC_BASE_URI:'https://admin.silentstream.win',DEVICE_BASE_URI:'https://agent.silentstream.win',GOOGLE_CLIENT_ID:'test-client',GOOGLE_CLIENT_SECRET:'test-secret',PUBLIC_M112_PLAYLISTS:enabled?'true':'false'};
  const plane=new ControlPlane({storage:{get:async key=>values.get(key)},getWebSockets:()=>[]},env);
  env.CONTROL={idFromName:()=> 'global',get:()=>({fetch:request=>plane.fetch(request)})};
  return {env,plane,values,request:(path='/api/public/m112/playlists',method='GET',host='silentstream.win')=>worker.fetch(new Request(`https://${host}${path}`,{method}),env)};
}
test('feed shares only five ready nonfuture M112 links and source time',()=>{
  const result=recentPublicPlaylists(device,new Date('2026-10-27T02:30:00Z'));
  assert.deepEqual(result.items.map(x=>x.date),['2026-10-27','2026-10-26','2026-10-23','2026-10-22','2026-10-21']);
  assert.deepEqual(Object.keys(result).sort(),['items','room','updatedAtUtc']);
  assert.deepEqual(Object.keys(result.items[0]).sort(),['date','status','url']);
  assert.equal(JSON.stringify(result).includes('never-share'),false);
});
test('disabled by default even through the internal data route',async()=>{
  const app=setup(false);assert.equal((await app.request()).status,404);
  assert.equal((await app.plane.publicPlaylists()).status,404);
});
test('enabled feed is readable without login and includes no credentials',async()=>{
  const response=await setup().request(); assert.equal(response.status,200);
  assert.equal(response.headers.get('access-control-allow-origin'),'*');
  assert.equal(response.headers.get('access-control-allow-credentials'),null);
  assert.equal(response.headers.get('set-cookie'),null);
  assert.equal((await response.json()).room,'m112');
});
test('room boundaries, agent host and write requests remain closed',async()=>{
  const app=setup();assert.equal((await app.request('/api/public/m111/playlists')).status,404);
  assert.equal((await app.request(undefined,'POST')).status,405);
  assert.equal((await app.request(undefined,'GET','agent.silentstream.win')).status,404);
  assert.equal((await app.request('/api/rooms','GET','admin.silentstream.win')).status,401);
  assert.equal((await app.request(undefined,'GET','m112.silentstream.win')).status,401);
});
test('HEAD returns no body and missing snapshot does not pretend to be empty history',async()=>{
  const head=await setup().request(undefined,'HEAD');assert.equal(head.status,200);assert.equal(await head.text(),'');
  assert.equal((await setup(true,{...device,playlists:null}).request()).status,503);
});
test('revoked or mismatched devices cannot supply public data, offline last snapshot can',async()=>{
  assert.equal((await setup(true,{...device,revokedAtUtc:'2026-10-26T00:00:00Z'}).request()).status,503);
  assert.equal((await setup(true,{...device,roomId:'m111'}).request()).status,503);
  assert.equal((await setup(true,{...device,playlists:{room:'m111',items:device.playlists.items}}).request()).status,503);
  const r=await (await setup().request()).json(); assert.equal(r.updatedAtUtc,device.lastSeenAtUtc);
});
