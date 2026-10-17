import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../public/room/app.js',import.meta.url),'utf8');
function setup(items=[],online=false,clock='2026-10-16T15:00:00Z'){
 const handlers={},fields={playlistDate:{value:''}};
 const context=vm.createContext({Date:class extends Date{constructor(...args){super(...(args.length?args:[clock]))}},Intl,
 device:{registered:true,online,playlists:items===null?null:{room:'m112',items}},roomId:'m112',roomName:()=> 'M112',
 RECENT_PLAYLIST_COUNT:5,HISTORY_PAGE_SIZE:5,playlistHistoryOpen:false,playlistHistoryAnchor:'',esc:String,
 $:id=>fields[id],roomEl:{addEventListener:(name,fn)=>handlers[name]=fn},todayPlaylistPanel:()=>'',render(){}});
 const names=['koreaToday','defaultHistoryDate','playlistEmptyMessage','selectPlaylistDate','playlistItems','playlistRow','historyPage','playlistPanel'];
 vm.runInContext(source.split('\n').filter(line=>names.some(name=>line.startsWith('function '+name+'('))||line.startsWith('roomEl.addEventListener("click",event=>{const button=')||line.startsWith('roomEl.addEventListener("change",')).join('\n'),context);
 return {context,fields,run:code=>vm.runInContext(code,context),click:attr=>handlers.click({target:{closest:()=>({dataset:{},hasAttribute:name=>name===attr})}}),change:value=>handlers.change({target:{id:'playlistDate',value}})};
}
const row=date=>({date,status:'ready',url:'https://www.youtube.com/playlist?list=test'});
test('opening history starts at today even when offline and unsynchronized',()=>{
 const app=setup(null);app.click('data-playlist-history');assert.equal(app.run('playlistHistoryAnchor'),'2026-10-17');
 assert.match(app.run('playlistPanel(device)'),/동기화된 재생목록 정보가 없습니다/);
});
test('sparse offline history starts at the latest available record',()=>{
 const app=setup(['2026-10-16','2026-10-12','2026-10-08'].map(row));app.click('data-playlist-history');
 assert.equal(app.run('historyPage(playlistItems()).rows[0].date'),'2026-10-16');
 app.change('2026-10-09');assert.equal(app.run('historyPage(playlistItems()).rows[0].date'),'2026-10-08');
});
test('an empty older range can navigate back to available recent items',()=>{
 const app=setup(['2026-10-16','2026-10-15'].map(row));app.click('data-playlist-history');
 app.change('2026-10-01');
 assert.equal(app.run('historyPage(playlistItems()).rows.length'),0);app.click('data-playlist-newer');
 assert.equal(app.run('historyPage(playlistItems()).rows[0].date'),'2026-10-16');
});
test('initial history date follows Korean midnight, year boundary and leap year',()=>{
 for(const [clock,expected] of [['2026-10-16T14:59:59Z','2026-10-16'],['2026-10-16T15:00:00Z','2026-10-17'],['2026-01-01T00:00:00Z','2026-01-01'],['2024-03-02T00:00:00Z','2024-03-02']])assert.equal(setup([],false,clock).run('defaultHistoryDate()'),expected);
});
test('reopening and polling both preserve the chosen date',()=>{
 const app=setup();app.click('data-playlist-history');app.change('2026-10-01');app.run('playlistPanel(device)');assert.equal(app.run('playlistHistoryAnchor'),'2026-10-01');
 app.click('data-playlist-history');app.click('data-playlist-history');assert.equal(app.run('playlistHistoryAnchor'),'2026-10-01');
});
test('future date clamps to today and missing snapshot differs from empty search',()=>{
 const app=setup();app.change('2026-12-01');assert.equal(app.run('playlistHistoryAnchor'),'2026-10-17');
 assert.match(setup(null,true).run('playlistEmptyMessage()'),/아직 동기화되지/);
 assert.match(app.run('playlistEmptyMessage()'),/선택한 날짜까지/);
});
