import {readFile,writeFile,open,unlink,mkdir} from 'node:fs/promises';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {join,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import webpush from 'web-push';
import {validSubscription,freshRequest,newMotion} from './core.mjs';
const run=promisify(execFile),here=dirname(fileURLToPath(import.meta.url));
const folder=join(process.env.LOCALAPPDATA,'USBHomeCamNAVER','remote');
const root='https://chome-a8970-default-rtdb.firebaseio.com';
const apiKey='AIzaSyBeA0nsSOJ9HbV7ZLt4XAcgYX6uWfP7ANo';
const cursorFile=join(folder,'push-cursor.json'),lockFile=join(folder,'push-sender.lock');
let uid,token,expiry=0,cursor={},lastLog='',submissions=0;
async function readJson(path,fallback={}){try{return JSON.parse((await readFile(path,'utf8')).replace(/^\uFEFF/,''));}catch{return fallback;}}
async function save(){await writeFile(cursorFile,JSON.stringify(cursor));}
async function authenticate(){
 if(Date.now()<expiry)return;
 const {stdout}=await run(join(here,'ReadPushSecrets.exe'),[],{windowsHide:true,maxBuffer:16384});
 const secrets=JSON.parse(stdout),vapid=JSON.parse(secrets.vapid);
 const response=await fetch('https://securetoken.googleapis.com/v1/token?key='+apiKey,{method:'POST',body:new URLSearchParams({grant_type:'refresh_token',refresh_token:secrets.refreshToken}),signal:AbortSignal.timeout(12000)});
 if(!response.ok)throw Error('authentication_failed');
 const data=await response.json();
 if(data.user_id!==secrets.uid)throw Error('owner_mismatch');
 uid=secrets.uid; token=data.id_token;expiry=Date.now()+Math.min(3000,Number(data.expires_in)-120)*1000;
 webpush.setVapidDetails(vapid.subject,vapid.publicKey,vapid.privateKey);
}
async function db(path,method='GET',body){
 await authenticate();
 const response=await fetch(root+'/clients/'+uid+'/push/'+path+'.json?auth='+encodeURIComponent(token),{method,body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(12000)});
 if(!response.ok){if(response.status===401)expiry=0;throw Error('database_'+response.status);}
 return response.json();
}
async function send(device,subscription,payload){
 if(!validSubscription(subscription))return {phase:'failed',detail:'invalid_subscription'};
 try{
  const response=await webpush.sendNotification(subscription,JSON.stringify(payload),{TTL:120,urgency:'high',timeout:12000});
  if(response.statusCode<200||response.statusCode>=300)throw Error('unexpected_status');
  submissions++;return {phase:'sent',detail:'push_service_accepted'};
 }catch(error){
  const status=Number(error.statusCode)||0;
  if(status===404||status===410)await db('subscriptions/'+device,'DELETE');
  return {phase:'failed',detail:status?'push_http_'+status:'push_network_error'};
 }
}
async function tick(){
 const subscriptions=await db('subscriptions')||{};
 const requests=await db('testRequests')||{};
 for(const [id,request] of Object.entries(requests).slice(-100)){
  if(!/^[a-f0-9]{32}$/.test(id))continue;
  const previous=await db('receipts/'+id);
  if(previous){await db('testRequests/'+id,'DELETE');continue;}
  let result={phase:'failed',detail:'expired_or_invalid_request'};
  // Persist a receipt before sending: a crash must never replay an old test.
  const deviceId=/^[a-f0-9]{32}$/.test(request?.deviceId||'')?request.deviceId:'0'.repeat(32);
  await db('receipts/'+id,'PUT',{deviceId,phase:'processing',detail:'sending',updatedAt:Date.now()});
  if(freshRequest(request,uid))result=await send(deviceId,subscriptions[deviceId],{type:'test',id});
  await db('receipts/'+id,'PUT',{deviceId,...result,updatedAt:Date.now()});
  await db('testRequests/'+id,'DELETE');
 }
 const state=await readJson(join(folder,'state.json'));
 const time=Date.parse(state.lastMotion)||0;
 if(cursor.motion===undefined){cursor.motion=time;await save();}
 if(newMotion(state,cursor.motion)&&Date.now()-Number(cursor.lastSent||0)>120000){
  cursor.motion=time;cursor.lastSent=Date.now();await save();
  for(const [id,subscription] of Object.entries(subscriptions).slice(0,20)){
   const result=await send(id,subscription,{type:'motion',id:String(time)});
   await db('receipts/'+id,'PUT',{deviceId:id,...result,updatedAt:Date.now()});
  }
 }
 await db('senderStatus','PUT',{updatedAt:Date.now(),online:true,devices:Object.keys(subscriptions).length,submissions});
}
async function heartbeat(ok,error=''){await writeFile(join(folder,'push-heartbeat.json'),JSON.stringify({updatedAt:new Date().toISOString(),ok,error})).catch(()=>{});}
async function main(){
 await mkdir(folder,{recursive:true});
 try{
  const handle=await open(lockFile,'wx');await handle.writeFile(String(process.pid));await handle.close();
 }catch{
  const pid=Number(await readFile(lockFile,'utf8').catch(()=>''));
  try{if(pid>0){process.kill(pid,0);return;}}catch{}
  await unlink(lockFile).catch(()=>{});
  const handle=await open(lockFile,'wx');await handle.writeFile(String(process.pid));await handle.close();
 }
 cursor=await readJson(cursorFile);
 try{
  do{
   try{await tick();lastLog='';await heartbeat(true);}
   catch(error){const safe=/^(authentication_failed|owner_mismatch|database_\d+)$/.test(error.message)?error.message:'sender_unavailable';if(safe!==lastLog){await writeFile(join(folder,'push-sender.log'),new Date().toISOString()+' '+safe+'\n',{flag:'a'});lastLog=safe;}await heartbeat(false,safe);}
   if(process.argv.includes('--once'))break;
   await new Promise(resolve=>setTimeout(resolve,5000));
  }while(true);
 }finally{await unlink(lockFile).catch(()=>{});}
}
main().catch(()=>{process.exitCode=1;});
