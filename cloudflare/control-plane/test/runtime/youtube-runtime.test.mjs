import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import * as runtime from 'miniflare';

test('native Workers fetch connects the owned channel and renews encrypted tokens',async()=>{
  const source=(await readFile(new URL('../../src/youtube-replay-jobs.js',import.meta.url),'utf8')).replaceAll('export ','');
  const options={workers:[
    {name:'subject',modules:true,compatibilityDate:'2026-09-01',outboundService:'youtube-fixture',script:source+`
      export default {async fetch(){
        const values=new Map([
          ['roomDevices:m112',['device-one']],
          ['device:device-one',{deviceId:'device-one',roomId:'m112',playlists:{items:[{status:'ready',playlistId:'PL_owned_playlist'}]}}]
        ]);
        let alarm=null;
        const storage={get:async k=>values.get(k),put:async(k,v)=>values.set(k,v),
          list:async({prefix})=>new Map([...values].filter(([k])=>k.startsWith(prefix))),
          transaction:fn=>fn(storage),getAlarm:async()=>alarm,setAlarm:async v=>{alarm=v},deleteAlarm:async()=>{alarm=null}};
        const env={YOUTUBE_TOKEN_KEY:btoa('01234567890123456789012345678901'),YOUTUBE_REPLAY_ROOMS:'m112',GOOGLE_CLIENT_ID:'test-client',GOOGLE_CLIENT_SECRET:'test-secret'};
        const jobs=new YouTubeReplayJobs({storage},env);
        const result=await jobs.register({access_token:'test-access',refresh_token:'test-refresh',scope:YOUTUBE_SCOPE},'m112');
        const grant=values.get('ytGrant:UC_owned_channel');
        const encrypted=!JSON.stringify(grant).includes('test-access')&&!JSON.stringify(grant).includes('test-refresh');
        grant.expiresAt=0;
        const renewed=await jobs.token(grant);
        const stored=await unseal(values.get('ytGrant:UC_owned_channel').encrypted,env.YOUTUBE_TOKEN_KEY,'UC_owned_channel');
        return Response.json({connected:result.connected,encrypted,renewed:renewed==='test-renewed',refreshPreserved:stored.refresh_token==='test-refresh',status:(await jobs.status('m112')).connected});
      }};
    `},
    {name:'youtube-fixture',modules:true,compatibilityDate:'2026-09-01',script:`
      export default {async fetch(request){
        const url=new URL(request.url);
        if(url.hostname==='oauth2.googleapis.com'&&url.pathname==='/token'){
          const form=new URLSearchParams(await request.text());
          if(form.get('refresh_token')!=='test-refresh'||form.get('grant_type')!=='refresh_token')return new Response(null,{status:400});
          return Response.json({access_token:'test-renewed',expires_in:3600});
        }
        if(request.headers.get('authorization')!=='Bearer test-access')return new Response(null,{status:401});
        if(url.pathname.endsWith('/channels')&&url.searchParams.get('mine')==='true')return Response.json({items:[{id:'UC_owned_channel'}]});
        if(url.pathname.endsWith('/playlists')&&url.searchParams.get('id')==='PL_owned_playlist')return Response.json({items:[{snippet:{channelId:'UC_owned_channel'}}]});
        return new Response(null,{status:404});
      }};
    `}
  ]};
  // Wrangler 4 uses Miniflare 4 or 5; the latter exports a compatibility converter.
  const mf=new runtime.Miniflare(runtime.convertV4MiniflareOptions?.(options)||options);
  try{
    const response=await mf.dispatchFetch('https://runtime.test/');
    assert.equal(response.status,200,await response.clone().text());
    assert.deepEqual(await response.json(),{connected:true,encrypted:true,renewed:true,refreshPreserved:true,status:true});
  }finally{await mf.dispose();}
});
