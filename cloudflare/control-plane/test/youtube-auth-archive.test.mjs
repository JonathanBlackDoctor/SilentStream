import test from 'node:test';
import assert from 'node:assert/strict';
import worker,{ControlPlane} from '../src/index.js';
import {YOUTUBE_SCOPE} from '../src/youtube-replay-jobs.js';
function setup(){
  const values=new Map([['ownerEmail','owner@example.com'],['member:manager@example.com',{role:'manager',rooms:['m112']}],
    ['roomDevices:m112',['device-one']],['device:device-one',{roomId:'m112',deviceId:'device-one',installationId:'install-one',
      lastSeenAtUtc:'2026-10-27T00:00:00Z',playlists:{room:'m112',items:[{date:'2026-10-27',status:'ready',playlistId:'PL_owned'}]}}]]);
  let lock=Promise.resolve(),alarm=null;
  const storage={get:async k=>structuredClone(values.get(k)),put:async(k,v)=>values.set(k,structuredClone(v)),delete:async k=>values.delete(k),
    list:async({prefix})=>new Map([...values].filter(([key])=>key.startsWith(prefix))),
    transaction:fn=>{const result=lock.then(()=>fn(storage));lock=result.catch(()=>{});return result},
    getAlarm:async()=>alarm,setAlarm:async v=>{alarm=v},deleteAlarm:async()=>{alarm=null}};
  const env={PUBLIC_BASE_URI:'https://admin.silentstream.win',DEVICE_BASE_URI:'https://agent.silentstream.win',GOOGLE_CLIENT_ID:'web-client',GOOGLE_CLIENT_SECRET:'web-secret',
    ENABLE_DEVELOPMENT_IDENTITY:'true',YOUTUBE_REPLAY_ROOMS:'m112',YOUTUBE_TOKEN_KEY:btoa('01234567890123456789012345678901'),
    ASSETS:{fetch:async request=>new Response(new URL(request.url).pathname)}};
  const plane=new ControlPlane({storage,getWebSockets:()=>[]},env);
  env.CONTROL={idFromName:()=> 'global',get:()=>({fetch:request=>plane.fetch(request)})};
  const request=(path,{host='admin.silentstream.win',user='owner@example.com',method='GET',origin,body}={})=>worker.fetch(new Request(`https://${host}${path}`,{
    method,headers:{...(user?{'x-silentstream-dev-email':user}:{}),...(origin?{origin}:{}),...(body?{'content-type':'application/x-www-form-urlencoded'}:{})},body}),env);
  return {values,plane,env,request};
}
test('archive HTML, source and feed require room-bound login and never become public assets',async()=>{
  const app=setup();
  assert.equal((await app.request('/archive/',{host:'m112.silentstream.win',user:null})).status,302);
  assert.equal((await app.request('/api/room/playlist-links',{host:'m112.silentstream.win',user:null})).status,401);
  const page=await app.request('/archive/',{host:'m112.silentstream.win',user:'manager@example.com'});
  assert.equal(page.status,200);assert.equal(await page.text(),'/archive/index.html');
  assert.equal(page.headers.get('cache-control'),'no-store');
  const feed=await app.request('/api/room/playlist-links',{host:'m112.silentstream.win',user:'manager@example.com'});
  assert.equal(feed.status,200);assert.equal(feed.headers.get('access-control-allow-origin'),null);
  assert.equal((await feed.json()).room,'m112');
  assert.equal((await app.request('/archive/playlists.json',{host:'m111.silentstream.win'})).status,404);
  assert.equal((await app.request('/archive/playlists.json',{host:'silentstream.win'})).status,302);
  assert.equal((await app.request('/api/public/m112/playlists',{host:'silentstream.win'})).status,404);
});
test('only administrators can connect a YouTube grant; ordinary room management is insufficient',async()=>{
  const app=setup();
  assert.equal((await app.request('/control/youtube',{user:'manager@example.com'})).status,403);
  const response=await app.request('/api/control/youtube/connect',{user:'manager@example.com',method:'POST',origin:app.env.PUBLIC_BASE_URI,body:'room=m112'});
  assert.equal(response.status,403);
  assert.equal([...app.values.keys()].some(k=>k.startsWith('oauthState:')),false);
});
test('connection requests reject foreign origins and request offline, scoped PKCE consent',async()=>{
  const app=setup();const opts={method:'POST',body:'room=m112'};
  assert.equal((await app.request('/api/control/youtube/connect',{...opts,origin:'https://evil.example'})).status,403);
  const response=await app.request('/api/control/youtube/connect',{...opts,origin:app.env.PUBLIC_BASE_URI});
  assert.equal(response.status,302);const target=new URL(response.headers.get('location'));
  assert.equal(target.origin,'https://accounts.google.com');assert.equal(target.searchParams.get('scope'),YOUTUBE_SCOPE);
  assert.equal(target.searchParams.get('access_type'),'offline');assert.equal(target.searchParams.get('code_challenge_method'),'S256');
  assert.equal(target.searchParams.get('redirect_uri'),app.env.PUBLIC_BASE_URI+'/auth/callback');
  const saved=[...app.values.entries()].find(([k])=>k.startsWith('oauthState:'))[1];
  assert.equal(saved.purpose,'youtube');assert.equal(saved.email,'owner@example.com');assert.equal(saved.roomId,'m112');
});
test('YouTube state is consumed once even by simultaneous callbacks',async()=>{
  const app=setup(),state='s'.repeat(64),verifier='v'.repeat(64);
  await app.plane.oauthBegin({purpose:'youtube',email:'owner@example.com',roomId:'m112',state,verifier});
  const result=await Promise.all([app.plane.oauthConsume({state}),app.plane.oauthConsume({state})]);
  assert.deepEqual(result.map(r=>r.status).sort(),[200,400]);
});
test('callback cannot attach a grant from a different initiating administrator session',async()=>{
  const app=setup(),state='s'.repeat(64),code='c'.repeat(64);
  await app.plane.oauthBegin({purpose:'youtube',email:'owner@example.com',roomId:'m112',state,verifier:'v'.repeat(64)});
  const response=await app.request(`/auth/callback?state=${state}&code=${code}`,{user:'different@example.com'});
  assert.equal(response.status,403);assert.equal([...app.values.keys()].some(k=>k.startsWith('ytGrant:')),false);
});
test('authorized callback encrypts the grant and redirects without exposing it to the browser',async t=>{
  const app=setup(),state='s'.repeat(64),code='c'.repeat(64),original=globalThis.fetch;
  const tokens={access_token:'private-access-fixture',refresh_token:'private-refresh-fixture',scope:YOUTUBE_SCOPE,expires_in:3600};
  globalThis.fetch=async()=>Response.json(tokens);t.after(()=>{globalThis.fetch=original});
  app.plane.replayJobs.fetch=async()=>Response.json({items:[{id:'UC_owned_channel'}]});
  await app.plane.oauthBegin({purpose:'youtube',email:'owner@example.com',roomId:'m112',state,verifier:'v'.repeat(64)});
  const response=await app.request(`/auth/callback?state=${state}&code=${code}`);
  assert.equal(response.status,302);assert.equal(new URL(response.headers.get('location')).pathname,'/control/youtube');
  assert.equal(await response.text(),'');assert.equal(JSON.stringify([...app.values]).includes(tokens.refresh_token),false);
  assert.ok(app.values.has('ytGrant:UC_owned_channel'));
});
