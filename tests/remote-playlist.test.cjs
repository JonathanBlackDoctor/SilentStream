const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const html=fs.readFileSync(path.join(__dirname,'../src/SilentStream.App/Remote/index.html'),'utf8');
const source=html.slice(html.indexOf('let todayPlaylist ='),html.indexOf('    function renderStatus(s)'));
const playlist={room:'m112',date:'2026-10-17',status:'ready',url:'https://www.youtube.com/playlist?list=PL_today'};
function setup(){
  let clock='2026-10-16T15:00:00Z';
  const fields={},copied=[],messages=[];
  const element=id=>fields[id]??={value:'',textContent:'',disabled:false,
    classList:{hidden:true,add(){this.hidden=true},remove(){this.hidden=false}},focus(){},select(){this.selected=true},setSelectionRange(){}};
  const context=vm.createContext({Date:class extends Date{constructor(...args){super(...(args.length?args:[clock]))}},Intl,
    cur:{label:'m112'},$:element,window:{isSecureContext:true},
    navigator:{clipboard:{writeText:async value=>copied.push(value)}},document:{execCommand:()=>false},toast:value=>messages.push(value)});
  vm.runInContext(source,context);
  return {context,fields,copied,messages,clock:value=>clock=value,
    render:value=>context.renderTodayPlaylist(value),click:()=>element('copyTodayPlaylistBtn').onclick()};
}
test('embedded remote copies the ready daily link',async()=>{
  const app=setup();app.render(playlist);assert.equal(app.fields.copyTodayPlaylistBtn.disabled,false);
  await app.click();assert.deepEqual(app.copied,[playlist.url]);assert.match(app.messages[0],/복사했습니다/);
});
test('embedded remote disables stale dates, missing data and failure states',async()=>{
  for(const value of [null,{...playlist,date:'2026-10-16'},...['missing','creating','auth-required','failed'].map(status=>({...playlist,status}))]){
    const app=setup();app.render(value);assert.equal(app.fields.copyTodayPlaylistBtn.disabled,true);
    await app.click();assert.equal(app.copied.length,0);
  }
});
test('embedded remote rechecks midnight at click time',async()=>{
  const app=setup();app.render(playlist);app.clock('2026-10-17T15:00:00Z');await app.click();
  assert.equal(app.copied.length,0);assert.equal(app.fields.copyTodayPlaylistBtn.disabled,true);
});
test('embedded remote exposes a selectable link if both copy APIs fail',async()=>{
  const app=setup();app.context.navigator.clipboard.writeText=async()=>{throw Error('denied')};
  app.render(playlist);await app.click();
  assert.equal(app.fields.playlistLink.value,playlist.url);assert.equal(app.fields.playlistLink.selected,true);
  assert.equal(app.fields.playlistLink.classList.hidden,false);assert.match(app.fields.playlistHint.textContent,/자동 복사가 차단/);
  app.render(playlist);assert.equal(app.fields.playlistLink.classList.hidden,false);
});
test('room change clears the previous link and prevents late copy completion from changing UI',async()=>{
  const app=setup();let finish;
  app.context.navigator.clipboard.writeText=()=>new Promise(resolve=>finish=resolve);
  app.render(playlist);const pending=app.click();
  app.context.cur={label:'m111'};app.render(null);finish();await pending;
  assert.equal(app.fields.playlistLink.value,'');assert.equal(app.fields.copyTodayPlaylistBtn.disabled,true);
  assert.equal(app.messages.length,0);
});
