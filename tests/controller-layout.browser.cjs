const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
// Run with a locally installed Playwright; optionally set SILENTSTREAM_PLAYWRIGHT to its module path.
const {chromium}=require(process.env.SILENTSTREAM_PLAYWRIGHT||'playwright');
const root=path.resolve(__dirname,'..'),web=path.join(root,'cloudflare/control-plane/public/room');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 const report=[],errors=[]; const out=process.env.SILENTSTREAM_LAYOUT_OUTPUT||path.join(root,'artifacts/controller-layout');fs.mkdirSync(out,{recursive:true});
 try{
 for(const width of [320,360,390,768,1280,640]){
  const context=await browser.newContext({viewport:{width,height:900},serviceWorkers:'block'}),page=await context.newPage();
  page.on('pageerror',e=>errors.push(e.message));
  const now=new Date().toISOString(),day=new Date(Date.now()+32400000).toISOString().slice(0,10);
  const device={roomId:'m112',registered:true,online:true,role:'admin',lastSeenAtUtc:now,agentVersion:'0.4.11',
   app:{updatedAtUtc:now,state:'Idle',quietShutdownControl:true,shutdownScheduleControl:true,remoteShutdownScheduleCommands:true,today:{date:day,periods:[]},queue:{items:[]}},
   playlists:{room:'m112',items:[{date:day,status:'ready',url:'https://www.youtube.com/playlist?list=PL_demo'}]},
   audio:{microphoneControl:true,remoteMicrophoneCommands:true,devices:[{id:'mic',name:'강의실 USB 마이크'}],sources:[]}};
  const powerCommands=[];
  await page.route('**/*',async route=>{
   const url=new URL(route.request().url());
   const json=value=>route.fulfill({json:value});
   if(url.pathname==='/api/control/session')return json({admin:true,roomName:'M112',email:'검토용 계정',centralUrl:'#'});
   if(url.pathname==='/api/control/rooms')return json([device]);
   if(url.pathname==='/api/control/members')return json([]);
   if(url.pathname.endsWith('/actions/system.shutdown')){powerCommands.push(route.request().postDataJSON());return json({ok:true,body:'{"ok":true,"message":"검토용 종료 응답"}'});}
   if(url.pathname.startsWith('/api/'))return route.fulfill({status:400,json:{error:'검토용 요청'}});
   let file=url.pathname==='/direct'?path.join(root,'src/SilentStream.App/Remote/index.html'):url.pathname==='/central'?path.join(web,'../control/index.html'):url.pathname.startsWith('/control/')?path.join(web,'..',url.pathname.slice(1)):path.join(web,url.pathname==='/'?'index.html':url.pathname.slice(1));
   if(url.pathname.startsWith('/icons/'))file=path.join(web,'icons/m112',path.basename(url.pathname));
   if(!fs.existsSync(file))return route.fulfill({status:404,body:''});
   return route.fulfill({contentType:file.endsWith('.js')?'application/javascript':file.endsWith('.png')?'image/png':'text/html; charset=utf-8',body:fs.readFileSync(file)});
  });
  await page.goto('https://m112.silentstream.test/');await page.locator('.controller').waitFor();
  await page.locator('[data-disclosure="advanced"] > summary').click();
  await page.locator('[data-disclosure="audio"] > summary').click();
  await page.locator('[data-disclosure="uploads"] > summary').click();
  if(width===640)await page.evaluate(()=>document.documentElement.style.zoom='2');
  const geometry=await page.evaluate(()=>({overflow:document.documentElement.scrollWidth>innerWidth,
   controls:[...document.querySelectorAll('button,input')].filter(e=>e.getClientRects().length).map(e=>({text:e.textContent||e.type,x:e.getBoundingClientRect().x,width:e.getBoundingClientRect().width,height:e.getBoundingClientRect().height}))}));
  assert.equal(geometry.overflow,false,`horizontal overflow at ${width}`);
  const inset=await page.locator('[data-action="live.start"]').evaluate(e=>e.getBoundingClientRect().left-e.closest('details').getBoundingClientRect().left);
  assert.ok(inset>=20,'broadcast button must be inset from card border');
  const wide=await page.locator('[data-shutdown-create]').evaluate(e=>Math.abs(e.getBoundingClientRect().width-e.parentElement.getBoundingClientRect().width)<1);
  assert.ok(wide,'single shutdown action must fill its row');
  await page.screenshot({path:path.join(out,`after-${width}.png`),fullPage:true});
  report.push({width,...geometry});
  if(width===390){
   const power=page.locator('[data-action="system.shutdown"]');
   assert.ok(await power.isEnabled());
   page.once('dialog',dialog=>dialog.dismiss());await power.click();assert.equal(powerCommands.length,0);
   page.once('dialog',dialog=>dialog.accept());await power.click();
   await page.waitForFunction(()=>document.getElementById('notice').textContent==='검토용 종료 응답');
   assert.equal(powerCommands.length,1);
   await page.locator('[data-playlist-history]').click();
   await page.locator('#playlistDate').fill('2026-10-12');
   await page.locator('#shutdown-m112').fill('2026-10-18T22:00');
   await page.locator('[data-refresh]').click();
   await page.waitForFunction(()=>document.getElementById('shutdown-m112')?.value==='2026-10-18T22:00');
   assert.equal(await page.locator('#playlistDate').inputValue(),'2026-10-12');
   assert.equal(await page.locator('[data-disclosure="advanced"]').getAttribute('open'),'');
   device.online=false;
   await page.locator('[data-refresh]').click();await page.getByText('PC 연결이 끊겨 있습니다',{exact:true}).waitFor();
   assert.ok(await page.locator('[data-shutdown-create]').isEnabled(),'offline cloud shutdown request stays available');
   assert.ok(await page.locator('[data-action="system.shutdown"]').isDisabled(),'offline immediate power is blocked');
   await page.goto('https://m112.silentstream.test/direct?demo=1');
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,'PC-direct mobile view overflows');
   await page.screenshot({path:path.join(out,'direct-mobile.png'),fullPage:true});
   device.online=true;
   await page.goto('https://m112.silentstream.test/central');await page.locator('[data-action="system.shutdown"]').waitFor();
   page.once('dialog',dialog=>dialog.dismiss());await page.locator('[data-action="system.shutdown"]').click();
   assert.equal(powerCommands.length,1);
   page.once('dialog',dialog=>dialog.accept());await page.locator('[data-action="system.shutdown"]').click();
   await page.waitForFunction(()=>document.getElementById('notice').textContent.length>0);
   assert.equal(powerCommands.length,2);
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
   await page.screenshot({path:path.join(out,'central-mobile.png'),fullPage:true});
  }
  await context.close();
 }
 assert.deepEqual(errors,[]);
 fs.writeFileSync(path.join(out,'after.json'),JSON.stringify({report,errors},null,2));console.log(JSON.stringify({viewports:report.map(r=>r.width),horizontalOverflow:report.some(r=>r.overflow),javascriptErrors:errors,checks:'44px targets, card inset, single action width, disclosure/date drafts, offline power controls, direct and central views'}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
