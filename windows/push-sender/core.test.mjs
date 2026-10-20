import {test} from 'node:test';
import assert from 'node:assert/strict';
import {validSubscription,freshRequest,newMotion} from './core.mjs';
test('push endpoints reject local addresses, URL credentials and suffix attacks',()=>{
 const s={endpoint:'https://fcm.googleapis.com/fcm/send/a',keys:{p256dh:'a'.repeat(87),auth:'b'.repeat(22)}};
 assert.equal(validSubscription(s),true);
 for(const endpoint of ['http://fcm.googleapis.com/a','https://127.0.0.1/a','https://fcm.googleapis.com.evil.test/a','https://user@fcm.googleapis.com/a','https://fcm.googleapis.com:444/a'])assert.equal(validSubscription({...s,endpoint}),false);
});
test('stale requests and motion replay are suppressed',()=>{
 const now=Date.now(),r={deviceId:'a'.repeat(32),requester:'owner',requestedAt:now};
 assert(freshRequest(r,'owner',now)); assert(!freshRequest(r,'other',now));assert(!freshRequest({...r,requestedAt:now-91000},'owner',now));
 const state={recording:true,lastMotion:new Date(now).toISOString()};
 assert(newMotion(state,now-1,now));assert(!newMotion(state,now,now));assert(!newMotion(state,0,now+91000));
});
