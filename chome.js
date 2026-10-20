import {makePushUI} from "./push-client.js";
import {initializeApp} from "https://www.gstatic.com/firebasejs/11.10.0/firebase-app.js";
import {getAuth,GoogleAuthProvider,signInWithPopup,signOut,onAuthStateChanged} from "https://www.gstatic.com/firebasejs/11.10.0/firebase-auth.js";
import {getDatabase,ref,onValue,set,remove} from "https://www.gstatic.com/firebasejs/11.10.0/firebase-database.js";
const app=initializeApp({apiKey:"AIzaSyBeA0nsSOJ9HbV7ZLt4XAcgYX6uWfP7ANo",authDomain:"chome-a8970.firebaseapp.com",databaseURL:"https://chome-a8970-default-rtdb.firebaseio.com",projectId:"chome-a8970",storageBucket:"chome-a8970.firebasestorage.app",messagingSenderId:"280390101020",appId:"1:280390101020:web:9b34e433a1ab2f4a0797d9"});
const auth=getAuth(app),db=getDatabase(app),$=id=>document.getElementById(id);
const pushUI=makePushUI({auth,db,ref,set,onValue,remove,$,text:(id,msg)=>$(id).textContent=String(msg)});
let user=null,stopState=null,stopAck=null,state=null,ack=null,lastCount=null,sequence=0,pending=null;
const text=(id,value)=>$(id).textContent=String(value);
const pretty=value=>{
 if(!value)return "없음";
 const d=new Date(value);return Number.isNaN(d.getTime())?"알 수 없음":d.toLocaleString("ko-KR");
};
const fmt=(detail)=>{
 const table={awaiting_recorder:"녹화기 처리 대기",recorder_state_confirmed:"녹화 상태 확인 완료",
 recorder_confirmation_timeout:"실제 녹화 상태 확인 시간 초과",recorder_rejected:"녹화기에서 거부",another_command_pending:"이전 명령 처리 중"};
 return table[detail]||detail||"";
};
function bridgeAlive(){
 const bridge=Number(state?.bridgeAt);
 return Number.isFinite(bridge)&&bridge>0&&Math.abs(Date.now()-bridge)<20000;
}
function redraw(){
 const bridge=bridgeAlive(),online=bridge&&state?.online===true;
 text("badge",!bridge?"연결 끊김":online?"PC 온라인":"PC 릴레이 온라인");
 text("status",!bridge?"PC 연결 대기":online?(state.recording?"녹화 중":"녹화 대기"):"녹화기 오프라인");
 text("updated",bridge?"최근 녹화기 상태: "+pretty(state?.updatedAt):"Firebase 상태가 오래되었습니다. PC 릴레이를 확인하세요.");
 text("camera",online?(state.cameraConnected?"연결됨":"미연결"):"-");
 text("cloud",online?(state.cloudConnected?"연결됨":"로그인 안 됨"):"-");
 text("pending",online?String(state.pendingUploads??0)+"건":"-");
 text("motion",online?(state.motionActive?"이벤트 녹화 중":"대기 중"):"-");
 text("lastMotion","최근 움직임 감지: "+pretty(state?.lastMotion));
 $("start").disabled=!user||!bridge||Boolean(pending);
 $("stop").disabled=!user||!bridge||Boolean(pending);
 const count=Number(state?.motionDetections||0);

}
function handleAck(value){
 ack=value;
 if(!pending||!value||value.id!==pending.id||Number(value.seq)!==pending.seq)return;
 if(value.phase==="queued"){
  text("feedback","PC가 명령을 접수했습니다. 실제 녹화 상태 확인 중…");
 }else if(value.phase==="applied"){
  text("feedback","녹화기 실행 확인: "+(pending.action==="START"?"녹화 시작":"녹화 중지")+" ("+pretty(value.updatedAt)+")");
  pending=null;redraw();
 }else if(value.phase==="failed"){
  text("feedback","명령 실패: "+fmt(value.detail));
  pending=null;redraw();
 }
}
function stopListeners(){
 if(stopState)stopState();if(stopAck)stopAck();
 stopState=stopAck=null;state=ack=null;pending=null;lastCount=null;redraw();
}
function listen(uid){
 stopListeners();
 stopState=onValue(ref(db,"clients/"+uid+"/status"),s=>{
  state=s.val();
  redraw();
 },e=>{state=null;redraw();text("feedback","상태 읽기 거부: "+e.code+" (소유자 규칙을 확인하세요)");});
 stopAck=onValue(ref(db,"clients/"+uid+"/ack"),s=>handleAck(s.val()),
 e=>text("feedback","명령 확인 읽기 오류: "+e.code));
}
$("login").onclick=async()=>{
 $("login").disabled=true;
 try{await signInWithPopup(auth,new GoogleAuthProvider());}
 catch(e){text("loginFeedback","로그인 실패: "+(e.code||e.message));}
 finally{$("login").disabled=false;}
};
onAuthStateChanged(auth,u=>{
 user=u;
 pushUI.setUser(u);
 if(u){
  $("loginPanel").classList.add("hidden");$("dashboard").classList.remove("hidden");
  listen(u.uid);
 }else{
  stopListeners();
  $("loginPanel").classList.remove("hidden");$("dashboard").classList.add("hidden");
  text("badge","로그인 필요");
 }
});
async function send(action){
 if(!user||pending||!bridgeAlive())return;
 if(action==="STOP"&&!confirm("녹화를 중지하시겠습니까? 업로드 대기 파일은 유지됩니다."))return;
 const id=crypto.randomUUID().replace(/-/g,"");
 sequence=Math.max(Date.now(),sequence+1);
 const seq=sequence;
 pending={id,seq,action};
 redraw();
 try{
  await set(ref(db,"clients/"+user.uid+"/command"),{id,seq,action,requester:user.uid,issuedAt:Date.now()});
  // An accepted write is NOT a recorder acknowledgement.
  text("feedback","Firebase에 명령 등록됨. PC 실제 실행 확인을 기다립니다.");
  if(ack?.id===id)handleAck(ack);
  setTimeout(()=>{
   if(pending?.id===id){text("feedback","실행 결과 확인 지연: PC 연결/녹화 상태를 확인하세요. 명령을 자동 재전송하지 않습니다.");pending=null;redraw();}
  },60000);
 }catch(e){
  if(pending?.id===id)pending=null;
  redraw();text("feedback","명령 등록 실패: "+(e.code||e.message));
 }
}
$("start").onclick=()=>send("START");
$("stop").onclick=()=>send("STOP");
$("logout").onclick=async()=>{try{await pushUI.disable();await signOut(auth);}catch(e){text("motionInfo","로그아웃 실패: "+(e.code||"연결을 확인하세요"));}};
$("link").onclick=async()=>{
 if(!user)return;
 const code=$("code").value.toUpperCase().replace(/[^0-9A-F]/g,"");
 if(code.length!==10){text("linkFeedback","10자리 페어링 코드를 입력하세요.");return;}
 $("link").disabled=true;
 try{
  const r=await fetch((location.hostname === "localhost" || location.hostname === "127.0.0.1") ? "/api/cloud/link" : "http://127.0.0.1:18742/api/cloud/link",{
   method:"POST",headers:{"Content-Type":"application/json"},
   body:JSON.stringify({code,uid:user.uid,refreshToken:user.refreshToken}),cache:"no-store"
  });
  const response=await r.json();
  if(!r.ok)throw Error(response.error||"HTTP "+r.status);
  $("code").value="";
  text("linkFeedback","PC 연결됨. Firebase에서 상태가 표시되는지 확인하세요.");
 }catch(e){text("linkFeedback","PC 연결 실패: "+e.message+" (최초 연결은 동일 Tailscale 네트워크가 필요합니다)");}
 finally{$("link").disabled=false;}
};
// A dead bridge cannot leave the last online snapshot visible indefinitely.
setInterval(redraw,3000);
