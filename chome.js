import {initializeApp} from "https://www.gstatic.com/firebasejs/11.10.0/firebase-app.js";
import {getAuth,GoogleAuthProvider,signInWithPopup,signOut,onAuthStateChanged} from "https://www.gstatic.com/firebasejs/11.10.0/firebase-auth.js";
import {getDatabase,ref,onValue,set} from "https://www.gstatic.com/firebasejs/11.10.0/firebase-database.js";
const app=initializeApp({apiKey:"AIzaSyBeA0nsSOJ9HbV7ZLt4XAcgYX6uWfP7ANo",authDomain:"chome-a8970.firebaseapp.com",databaseURL:"https://chome-a8970-default-rtdb.firebaseio.com",projectId:"chome-a8970",storageBucket:"chome-a8970.firebasestorage.app",messagingSenderId:"280390101020",appId:"1:280390101020:web:9b34e433a1ab2f4a0797d9"});
const auth=getAuth(app),db=getDatabase(app),$=id=>document.getElementById(id);
let user=null,stopState=null,stopAck=null,lastCount=null,sequence=0,commandBusy=false;
const setText=(id,v)=>$(id).textContent=v;
const pretty=d=>{if(!d)return "없음";let dt=new Date(d);return isNaN(dt.getTime())?"없음":dt.toLocaleString("ko-KR")};
function render(state){
 const online=state?.online===true && Date.now()-Number(state.bridgeAt||0)<20000;
 setText("badge",online?"PC 온라인":"PC 오프라인");
 setText("status",online?(state.recording?"🔴 녹화 중":"■ 녹화 중지"):"PC 연결 대기");
 setText("updated",online?"마지막 상태 "+pretty(state.updatedAt):"PC와 Firebase 연결을 확인하세요");
 setText("camera",online?(state.cameraConnected?"연결됨":"미연결"):"—");
 setText("cloud",online?(state.cloudConnected?"연결됨":"미연결"):"—");
 setText("pending",online?String(state.pendingUploads??0)+"개":"—");
 setText("motion",online?(state.motionActive?"이벤트 녹화":"감시 중"):"—");
 setText("lastMotion","최근 움직임: "+pretty(state?.lastMotion));
 const count=Number(state?.motionDetections||0);
 if(lastCount!==null && count>lastCount && online && "Notification" in window && Notification.permission==="granted"){
  new Notification("CHome 움직임 감지",{body:"움직임 감지: "+pretty(state.lastMotion),tag:"chome-"+count});
 }
 lastCount=count;
}
function listen(uid){
 if(stopState)stopState();if(stopAck)stopAck();lastCount=null;render(null);
 stopState=onValue(ref(db,"clients/"+uid+"/status"),s=>render(s.val()),e=>{render(null);setText("feedback","Firebase 상태 오류: "+e.code)});
 stopAck=onValue(ref(db,"clients/"+uid+"/ack"),s=>{
  let data=s.val();
  if(data?.seq)setText("feedback","PC 명령 수신: "+(data.action==="START"?"녹화 시작":"녹화 중지")+" ("+pretty(Number(data.acceptedAt))+")");
 },e=>setText("feedback","명령 확인 오류: "+e.code));
}
$("login").onclick=async()=>{
 $("login").disabled=true;
 try{await signInWithPopup(auth,new GoogleAuthProvider());}
 catch(e){setText("loginFeedback","로그인 실패: "+e.code+" · Google 로그인과 승인된 도메인 설정을 확인하세요.")}
 finally{$("login").disabled=false}
};
onAuthStateChanged(auth,u=>{
 user=u;
 if(u){
  $("loginPanel").classList.add("hidden");$("dashboard").classList.remove("hidden");
  listen(u.uid);
 }else{
  if(stopState)stopState();if(stopAck)stopAck();stopState=stopAck=null;
  $("loginPanel").classList.remove("hidden");$("dashboard").classList.add("hidden");
  setText("badge","로그인 대기");
 }
});
async function send(action){
 if(!user||commandBusy)return;
 if(action==="STOP"&&!confirm("녹화를 중지할까요? 움직임 감지도 중지됩니다."))return;
 commandBusy=true;$("start").disabled=$("stop").disabled=true;
 try{
  sequence=Math.max(Date.now(),sequence+1);
  await set(ref(db,"clients/"+user.uid+"/command"),{seq:sequence,action,requester:user.uid});
  setText("feedback",(action==="START"?"시작":"중지")+" 명령을 Firebase에 보냈습니다. PC 수신을 기다립니다.");
 }catch(e){setText("feedback","명령 전송 실패: "+e.code+" · Firebase 보안 규칙을 확인하세요.")}
 finally{commandBusy=false;$("start").disabled=$("stop").disabled=false}
}
$("start").onclick=()=>send("START");
$("stop").onclick=()=>send("STOP");
$("logout").onclick=()=>signOut(auth);
$("notify").onclick=async()=>{
 if(!("Notification" in window)){setText("motionInfo","이 브라우저는 알림을 지원하지 않습니다.");return}
 try{$("notify").textContent=(await Notification.requestPermission())==="granted"?"알림 허용됨":"알림 권한 필요"}
 catch(e){setText("motionInfo","알림 설정 실패: "+e.message)}
};
$("link").onclick=async()=>{
 if(!user)return;
 let code=$("code").value.toUpperCase().replace(/[^0-9A-F]/g,"");
 if(code.length!==10){setText("linkFeedback","10자리 코드를 입력하세요.");return}
 $("link").disabled=true;
 try{
  let r=await fetch("https://box1.tail23cca9.ts.net:8443/api/cloud/link",{
   method:"POST",headers:{"Content-Type":"application/json"},
   body:JSON.stringify({code,uid:user.uid,refreshToken:user.refreshToken}),cache:"no-store"
  });
  let response=await r.json();
  if(!r.ok)throw Error(response.error||("HTTP "+r.status));
  $("code").value="";
  setText("linkFeedback","PC 연결 성공! 앞으로 녹화 명령은 Firebase로 전달됩니다.");
 }catch(e){setText("linkFeedback","PC 연결 실패: "+e.message+" · 최초 연결은 Tailscale이 필요합니다.")}
 finally{$("link").disabled=false}
};
