/**
 * One-time owner-approved push credential transfer from this Windows PC to its
 * paired Y700. The VAPID private key is never logged or committed to source.
 * Requires existing DPAPI secrets and the already-running CHome desktop account.
 */
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {readFile} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {fileURLToPath} from 'node:url';
const run=promisify(execFile);
const here=dirname(fileURLToPath(import.meta.url));
const root=dirname(here);
const senderDir=join(root,'windows','push-sender');
async function main(){
 const {stdout}=await run(join(senderDir,'ReadPushSecrets.exe'),[],{windowsHide:true,maxBuffer:16384});
 const secrets=JSON.parse(stdout);
 const vapid=JSON.parse(secrets.vapid);
 const pub=JSON.parse(await readFile(join(root,'push-config.json'),'utf8'));
 if(!pub.enabled||vapid.publicKey!==pub.publicKey)throw Error('public_vapid_mismatch');
 if(!/^https:\/\//.test(vapid.subject)||!vapid.privateKey)throw Error('vapid_invalid');
 const sender=await readFile(join(senderDir,'sender.mjs'),'utf8');
 const key=sender.match(/const apiKey='([^']+)'/)?.[1];
 if(!key)throw Error('missing_local_firebase_config');
 const response=await fetch('https://securetoken.googleapis.com/v1/token?key='+encodeURIComponent(key),{
   method:'POST',body:new URLSearchParams({grant_type:'refresh_token',refresh_token:secrets.refreshToken}),
   signal:AbortSignal.timeout(15000)});
 if(!response.ok)throw Error('firebase_authentication_failed');
 const auth=await response.json();
 if(auth.user_id!==secrets.uid)throw Error('owner_identity_mismatch');
 const api='https://chome-a8970-default-rtdb.firebaseio.com/clients/'+encodeURIComponent(secrets.uid);
 async function get(path){
  const r=await fetch(api+path+'.json?auth='+encodeURIComponent(auth.id_token),{signal:AbortSignal.timeout(12000)});
  if(!r.ok)throw Error('firebase_preflight_'+r.status);
  return r.json();
 }
 const paired=await get('/devices/y700/authUid');
 if(!/^[A-Za-z0-9_-]{15,128}$/.test(paired||''))throw Error('no_paired_tablet');
 const r=await fetch(api+'/devices/y700/pushVapid.json?auth='+encodeURIComponent(auth.id_token),{
   method:'PUT',body:JSON.stringify({subject:vapid.subject,publicKey:vapid.publicKey,privateKey:vapid.privateKey}),
   signal:AbortSignal.timeout(15000)});
 if(!r.ok)throw Error('firebase_provisioning_'+r.status);
 console.log('Y700_PUSH_PROVISIONED=true; EXISTING_VAPID_REUSED=true; PAIRED_DEVICE_PRESENT=true');
}
main().catch(e=>{console.log('Y700_PUSH_PROVISION_FAILED='+e.message);process.exitCode=1;});