using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

internal sealed partial class CamForm {
 DateTime remoteLastStatus=DateTime.MinValue;
 string remotePath{get{return Path.Combine(data,"remote");}}
 string remoteCommands{get{return Path.Combine(remotePath,"commands");}}
 bool remoteControlStopped=false;
 void InitRemoteControl(){
  Directory.CreateDirectory(remoteCommands);
  var file=Path.Combine(remotePath,"desired.txt");
  try{if(File.Exists(file)&&File.ReadAllText(file).Trim()=="STOP"){autoRecordPending=false;remoteControlStopped=true;}}
  catch(Exception){autoRecordPending=true;}
  // This runs in the form constructor before the UI log control is initialized.
 }
 void RemoteTick(){
  try{
   Directory.CreateDirectory(remoteCommands);
   var files=Directory.GetFiles(remoteCommands,"*.cmd").OrderBy(x=>x).Take(15).ToArray();
   foreach(string f in files){
    string action="";
    try{action=File.ReadAllText(f,Encoding.UTF8).Trim().ToUpperInvariant();}catch{}
    if(action=="START"){
     remoteControlStopped=false;
     PersistRemoteDesired("START");
     if(!recording){
      autoRecordPending=true;
      if(!string.IsNullOrEmpty(cameraName))StartRecording();
      else if(!scanning)ScanCameras();
     }
     Log("휴대폰 명령: 녹화 시작 요청");
    }else if(action=="STOP"){
     remoteControlStopped=true;
     PersistRemoteDesired("STOP");
     autoRecordPending=false;
     if(recording)StopRecording();
     Log("휴대폰 명령: 녹화 중지 요청");
    }
    try{File.Delete(f);}catch{}
   }
   ProcessFirebaseCloudCommands();
   if((DateTime.Now-remoteLastStatus).TotalMilliseconds>=950){
    remoteLastStatus=DateTime.Now;
    string status=Path.Combine(remotePath,"state.json");
    var json=new JavaScriptSerializer().Serialize(new {
     updatedAt=DateTime.UtcNow.ToString("o"),
     running=true,
     recording=recording,
     mode=motionOnly?"MOTION":"CONTINUOUS",
     cameraConnected=!string.IsNullOrEmpty(cameraName),
     cloudConnected=loggedIn,
     uploading=uploadBusy,
     pendingUploads=pendingUploads,
     motionActive=motionOnly?motionEventActive:motionWindow.Active,
     motionDetections=motionPositiveClips,
     lastMotion=motionWindow.LastMotion==DateTime.MinValue?"":motionWindow.LastMotion.ToString("o"),
     lastSegment=lastCaptureFile,
     lastUpload=lastUpload,
     uploadedThisSession=completedUploads,
     uploadFailures=failedUploads
    });
    string tmp=status+".new";
    File.WriteAllText(tmp,json,Encoding.UTF8);
    if(File.Exists(status)){File.Delete(status);}
    File.Move(tmp,status);
   }
  }catch(Exception e){if((DateTime.Now-remoteLastStatus).TotalSeconds>8)Log("원격 상태 기록 오류: "+Limit(e.Message,120));}
 }
 void PersistRemoteDesired(string value){
  try{
   Directory.CreateDirectory(remotePath);
   File.WriteAllText(Path.Combine(remotePath,"desired.txt"),value,Encoding.UTF8);
  }catch(Exception e){Log("원격 동작 저장 실패: "+Limit(e.Message,90));}
 }
 // Parent Codex integration candidate, deliberately NOT applied to the live parent.
 // Dedicated cloud queue prevents legacy plain-text commands from consuming v2 JSON.
 void ProcessFirebaseCloudCommands(){
  string q=Path.Combine(remotePath,"cloud-commands");
  string results=Path.Combine(remotePath,"cloud-results");
  if(!Directory.Exists(q))return;
  Directory.CreateDirectory(results);
  var files=Directory.GetFiles(q,"*.cmd").OrderBy(x=>x).Take(5).ToArray();
  foreach(var f in files){
   string id="",action="";long seq=0,issued=0;
   try{
    var d=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(f,Encoding.UTF8));
    id=Convert.ToString(d["id"]);action=Convert.ToString(d["action"]);seq=Convert.ToInt64(d["seq"]);issued=Convert.ToInt64(d["issuedAt"]);
    string requestedMode=d.ContainsKey("mode")?Convert.ToString(d["mode"]):"";
    if(requestedMode!=""&&requestedMode!="MOTION"&&requestedMode!="CONTINUOUS"){File.Delete(f);continue;}
    if(!System.Text.RegularExpressions.Regex.IsMatch(id??"","^[a-f0-9]{32}$") ||
       seq<=0 || (action!="START"&&action!="STOP")){File.Delete(f);continue;}
    string receipt=Path.Combine(results,id+".json");
    if(File.Exists(receipt)){File.Delete(f);continue;}
    string phase="handled",detail="action_processed";
    try{
     long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
     if(issued<now-90000 || issued>now+15000)throw new InvalidOperationException("expired_command");
     if(action=="START"){
      if(requestedMode=="MOTION"||requestedMode=="CONTINUOUS")SetRecordingMode(requestedMode=="MOTION");
      remoteControlStopped=false;PersistRemoteDesired("START");
      if(!recording){autoRecordPending=true;if(!string.IsNullOrEmpty(cameraName))StartRecording();else if(!scanning)ScanCameras();}
     }else{
      remoteControlStopped=true;PersistRemoteDesired("STOP");
      autoRecordPending=false;if(recording)StopRecording();
     }
    }catch(Exception){phase="failed";detail="recorder_action_error";}
    var value=new JavaScriptSerializer().Serialize(new{
     id=id,seq=seq,action=action,phase=phase,detail=detail,
     handledAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    });
    string tmp=receipt+".part";
    File.WriteAllText(tmp,value,Encoding.UTF8);File.Move(tmp,receipt);
    File.Delete(f);
   }catch(Exception){
    // Leave file to retry on next tick if receipt could not be durably written.
   }
  }
 }
}