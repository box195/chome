const $=id=>document.getElementById(id);
const defaultOrigin="https://box1.tail23cca9.ts.net:8443";
const apiBase=(location.hostname.endsWith(".github.io")?defaultOrigin:location.origin);
const savedKey="homecam.session.v1";
let token=localStorage.getItem(savedKey)||"", busy=false,first=true,lastMotion="",lastCount=0;
function statusBadge(msg,good=false){$("connection").textContent=msg;$("connection").className="tag "+(good?"good":"bad")}
function showPair(){token="";localStorage.removeItem(savedKey);$("pairPanel").classList.remove("hidden");$("dashboard").classList.add("hidden");statusBadge("연결 필요")}
function showDash(){$("pairPanel").classList.add("hidden");$("dashboard").classList.remove("hidden")}
async function call(path,method="GET",body=null){
 const ac=new AbortController();const to=setTimeout(()=>ac.abort(),6500);
 try{
  let headers={"Accept":"application/json"};
  if(token)headers["X-HomeCam-Token"]=token;
  if(body!==null)headers["Content-Type"]="application/json";
  const r=await fetch(apiBase+path,{method,headers,body:body===null?undefined:JSON.stringify(body),signal:ac.signal,cache:"no-store"});
  const data=await r.json().catch(()=>({error:"invalid_response"}));
  if(r.status===401 && path!=="/api/pair"){showPair();throw Error("연결을 다시 인증해야 합니다")}
  if(!r.ok)throw Error(data.error||("HTTP "+r.status));
  return data;
 }finally{clearTimeout(to)}
}
async function pair(){
 const code=$("code").value.toUpperCase().replace(/[^0-9A-F]/g,"");
 if(code.length!==10){$("pairFeedback").textContent="10자리 연결 코드를 입력하세요.";return}
 $("pair").disabled=true;
 try{
  const r=await call("/api/pair","POST",{code});
  token=r.token;localStorage.setItem(savedKey,token);$("code").value="";first=true;showDash();await refresh();
 }catch(e){$("pairFeedback").textContent="연결 실패: "+e.message+" · 휴대폰에서 Tailscale이 연결되어 있는지 확인하세요."}
 finally{$("pair").disabled=false}
}
function fmt(iso){if(!iso)return "없음";let d=new Date(iso);return isNaN(d.getTime())?"없음":d.toLocaleTimeString("ko-KR",{hour:"2-digit",minute:"2-digit",second:"2-digit"})}
async function refresh(){
 if(!token||busy)return;busy=true;
 try{
  const d=await call("/api/status");
  statusBadge(d.online?"PC 온라인":"PC 오프라인",!!d.online);
  $("bigStatus").textContent=d.online?(d.recording?"● 녹화 중":"■ 녹화 중지"):"PC 연결 대기";
  $("recordingLight").textContent=d.online&&d.recording?"🔴":"⚪";
  $("lastSeen").textContent=d.online?"최근 상태: "+fmt(d.updatedAt):"PC 프로그램 또는 네트워크 연결을 확인하세요";
  $("camera").textContent=d.online?(d.cameraConnected?"연결됨":"미연결"):"—";
  $("cloud").textContent=d.online?(d.cloudConnected?"정상":"연결 안 됨"):"—";
  $("pending").textContent=d.online?(d.pendingUploads??0)+"개":"—";
  $("motion").textContent=d.online?(d.motionActive?"이벤트 녹화": "감시 중"):"—";
  $("activity").textContent=d.lastMotion?"마지막 움직임 "+fmt(d.lastMotion)+" · 누적 감지 "+(d.motionDetections||0)+"회":"아직 감지된 움직임이 없습니다.";
  const count=Number(d.motionDetections||0);
  if(!first && d.online && count>lastCount && d.lastMotion!==lastMotion){
   if("Notification" in window&&Notification.permission==="granted"){
    new Notification("HomeCam 움직임 감지",{body:"카메라 움직임 감지 · "+fmt(d.lastMotion),tag:"homecam-motion-"+count});
   }
   if(navigator.vibrate)navigator.vibrate([80,50,80]);
  }
  first=false;lastCount=count;lastMotion=d.lastMotion||"";
 }catch(e){statusBadge("연결 오류");$("commandFeedback").textContent="상태 확인 실패: "+e.message}
 finally{busy=false}
}
async function command(action){
 if(action==="stop"&&!confirm("원격으로 녹화를 중지할까요? 움직임 감지도 함께 멈춥니다."))return;
 const btn=$(action);btn.disabled=true;
 try{
  const res=await call("/api/"+action,"POST",{});
  $("commandFeedback").textContent=(action==="start"?"녹화 시작":"녹화 중지")+" 명령을 보냈습니다. PC 반영을 확인하는 중입니다.";
  setTimeout(refresh,1200);
 }catch(e){$("commandFeedback").textContent="명령 실패: "+e.message}
 finally{btn.disabled=false}
}
$("pair").addEventListener("click",pair);
$("code").addEventListener("keydown",e=>{if(e.key==="Enter")pair()});
$("start").addEventListener("click",()=>command("start"));
$("stop").addEventListener("click",()=>command("stop"));
$("refresh").addEventListener("click",refresh);
$("logout").addEventListener("click",showPair);
$("enableNotifications").addEventListener("click",async()=>{
 if(!("Notification" in window)){$("activity").textContent="이 브라우저에서는 알림을 지원하지 않습니다.";return;}
 const answer=await Notification.requestPermission();
 $("enableNotifications").textContent=answer==="granted"?"알림 허용됨":"알림 권한 필요";
});
if(token){showDash();refresh()}else{showPair()}
setInterval(()=>{if(token)refresh()},1800);
