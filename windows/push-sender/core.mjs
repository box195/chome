export function validSubscription(s) {
 try {
  const u=new URL(s.endpoint);
  return u.protocol==='https:' && !u.username && !u.password && (!u.port||u.port==='443') &&
   ['fcm.googleapis.com','android.googleapis.com'].includes(u.hostname) &&
   s.endpoint.length<4096 && /^[A-Za-z0-9_-]{87}=?$/.test(s.keys.p256dh) && /^[A-Za-z0-9_-]{22}={0,2}$/.test(s.keys.auth);
 } catch { return false; }
}
export function freshRequest(r,uid,now=Date.now()) {
 return r?.requester===uid && /^[a-f0-9]{32}$/.test(r.deviceId||'') && Number.isFinite(r.requestedAt) && r.requestedAt>=now-90000 && r.requestedAt<=now+15000;
}
export function newMotion(state,cursor,now=Date.now()) {
 const time=Date.parse(state?.lastMotion);
 return Number.isFinite(time) && time>Number(cursor||0) && now-time<90000 && now-time>=-15000 && state.recording===true;
}
