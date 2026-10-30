import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';

for(const view of ['room','control']){
 const source=await readFile(new URL(`../public/${view}/app.js`,import.meta.url),'utf8');
 const code=source.slice(source.indexOf('const dangerousActions='),source.indexOf(view==='room'?'async function copyPlaylist(':'function showDetails('));
 for(const action of ['system.shutdown','app.restart','live.stopBroadcast','live.stopAll','live.retry','inspection.rerun']){
  test(`${view}: ${action} asks once and cancellation sends no command`,async()=>{
   let accepted=false;const questions=[],calls=[];
   const context=vm.createContext({device:{},roomId:'m112',actionPending:false,
    controllerFresh:()=>true,roomName:()=> 'M112',render(){},show(){},load:async()=>{},setTimeout(){},
    confirm:message=>{questions.push(message);return accepted},
    api:async(path,options)=>{calls.push({path,options});return {ok:true,body:'{"ok":true}'}}});
   vm.runInContext(code,context);
   const invoke=()=>vm.runInContext(`act(${view==='room'?'':'"m112",'}${JSON.stringify(action)})`,context);
   await invoke();assert.equal(calls.length,0);assert.equal(questions.length,1);
   accepted=true;await invoke();assert.equal(calls.length,1);assert.equal(questions.length,2);
   assert.match(questions[0],/M112/);assert.match(calls[0].path,new RegExp(`/m112/actions/${action}$`));
   assert.equal(calls[0].options.body,'{}');
  });
 }
 test(`${view}: an in-flight command cannot be sent twice`,async()=>{
  let finish;let count=0;
  const context=vm.createContext({device:{},roomId:'m112',actionPending:false,
    controllerFresh:()=>!context.actionPending,roomName:()=> 'M112',render(){},show(){},load:async()=>{},setTimeout(){},
    confirm:()=>true,api:()=>{count++;return new Promise(resolve=>{finish=resolve})}});
  vm.runInContext(code,context);
  const first=vm.runInContext(`act(${view==='room'?'':'"m112",'}"app.restart")`,context);
  await vm.runInContext(`act(${view==='room'?'':'"m112",'}"app.restart")`,context);
  assert.equal(count,1);
  finish({ok:true,body:'{}'});await first;
 });
}
