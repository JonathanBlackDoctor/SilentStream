import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../public/room/app.js", import.meta.url), "utf8");
const helpers = source.slice(source.indexOf("function todayPlaylist("), source.indexOf("function playlistPanel("));
const dateHelper = source.split("\n").find(line => line.startsWith("function koreaToday("));
const copyHelper = source.split("\n").find(line => line.startsWith("async function copyPlaylist("));
const url = "https://www.youtube.com/playlist?list=PL_today";
const item = (date="2026-10-17", status="ready") => ({date,status,url});

function setup(items=[item()], room="m112") {
  let clock = "2026-10-16T15:00:00Z", click;
  const copied=[], notices=[], fields={};
  const field=id=>fields[id]??=( {value:"",showModal(){this.open=true},focus(){},select(){this.selected=true}} );
  const context=vm.createContext({
    Date:class extends Date {constructor(...args){super(...(args.length?args:[clock]))}}, Intl,
    roomId:"m112", device:{playlists:{room,items}}, roomName:()=>"M112", esc:String,
    roomEl:{addEventListener(_,handler){click=handler}}, render(){},
    window:{isSecureContext:true}, navigator:{clipboard:{writeText:async value=>copied.push(value)}},
    $:field, show:message=>notices.push(message)
  });
  vm.runInContext(dateHelper+"\n"+copyHelper+"\n"+helpers,context);
  return {context,copied,notices,fields,clock:value=>clock=value,
    click:()=>click({target:{closest:()=>true}}),
    panel:()=>vm.runInContext("todayPlaylistPanel(device)",context)};
}

test("today action copies exactly the current Korean date and room",async()=>{
  const app=setup([item("2026-10-16"),item(),item("2026-10-18")]);
  assert.match(app.panel(),/오늘 재생목록 링크 복사/);
  assert.doesNotMatch(app.panel(),/disabled/);
  app.click();await Promise.resolve();
  assert.deepEqual(app.copied,[url]);
});

test("yesterday, future, and other-room records cannot enable today's button",()=>{
  for(const app of [setup([item("2026-10-16")]),setup([item("2026-10-18")]),setup([item()],"m111"),setup([])]){
    assert.match(app.panel(),/disabled/);app.click();assert.equal(app.copied.length,0);
  }
});

test("creation and authentication failures explain why copy is unavailable",()=>{
  for(const [status,message] of [["creating","생성하고"],["auth-required","계정 인증"],["failed","생성에 실패"]]){
    const app=setup([item("2026-10-17",status)]);
    assert.match(app.panel(),/disabled/);assert.ok(app.panel().includes(message));
    app.click();assert.equal(app.copied.length,0);
  }
});

test("a button rendered before Korean midnight cannot copy yesterday after midnight",()=>{
  const app=setup();assert.doesNotMatch(app.panel(),/disabled/);
  app.clock("2026-10-17T15:00:00Z");app.click();
  assert.equal(app.copied.length,0);assert.match(app.notices[0],/준비되지/);
});

test("denied clipboard permission exposes a selectable link without claiming success",async()=>{
  const app=setup();
  app.context.navigator.clipboard.writeText=async()=>{throw Error("NotAllowedError")};
  app.click();await new Promise(resolve=>setImmediate(resolve));
  assert.equal(app.fields.playlistCopyAddress.value,url);
  assert.equal(app.fields.playlistCopyAddress.selected,true);
  assert.equal(app.fields.playlistCopyHelp.open,true);
  assert.match(app.notices[0],/자동 복사가 차단/);
});
