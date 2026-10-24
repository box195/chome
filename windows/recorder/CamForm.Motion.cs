using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;

internal sealed partial class CamForm {
 readonly FixedCameraMotion motionDetector=new FixedCameraMotion();
 readonly MotionWindow motionWindow=new MotionWindow();
 readonly ConcurrentQueue<string> motionFileQueue=new ConcurrentQueue<string>();
 readonly object motionGuard=new object();
 int motionWorkerActive=0;
 string motionFolder="";
 string lastMotionFile="";
 DateTime motionLastCheck=DateTime.MinValue;
 int motionEvaluatedClips=0,motionPositiveClips=0,motionEventClips=0,motionArchiveFailures=0;
 bool motionTestMode=false;
 NotifyIcon motionTray;
 Button motionTestButton;
 Label motionStatusLabel;

 void SetupMotionUI(FlowLayoutPanel tools,FlowLayoutPanel settingsPanel) {
   motionTestButton=Btn("감지 알림 시험",delegate {ForceMotionTest();});
   motionTestButton.Width=125;
   tools.Controls.Add(motionTestButton);
   motionStatusLabel=new Label{Text="움직임 감시: 준비 중",Width=440,Height=27,
    TextAlign=System.Drawing.ContentAlignment.MiddleLeft,ForeColor=Color.FromArgb(27,91,117)};
   foreach(var old in settingsPanel.Controls.OfType<Label>().ToArray()){settingsPanel.Controls.Remove(old);old.Dispose();}
   settingsPanel.Controls.Add(motionStatusLabel);
   motionTray=new NotifyIcon{
     Icon=SystemIcons.Information,Text="USBHomeCam-NAVER 움직임 감지",Visible=true
   };
 }
 void UpdateMotionStatus() {
  if(motionStatusLabel==null || motionStatusLabel.IsDisposed)return;
  string status;
  lock(motionGuard){
   status=!recording?"감시·녹화 중지":(motionOnly?motionEventActive:motionWindow.Active)?"움직임 감지 · 이벤트 녹화 중":"움직임 감시 중";
   status+=" · 감지 "+motionPositiveClips+"회";
   if(motionWindow.LastMotion!=DateTime.MinValue)status+=" · 최근 "+motionWindow.LastMotion.ToString("HH:mm:ss");
  }
  motionStatusLabel.Text=status;
 }
 void QueueMotionProbe(string path) {
  if(string.IsNullOrEmpty(path)||!path.EndsWith(".ts",StringComparison.OrdinalIgnoreCase))return;
  motionFileQueue.Enqueue(path);
  if(Interlocked.CompareExchange(ref motionWorkerActive,1,0)==0)
   Task.Run((Action)RunMotionWorker);
 }
 void RunMotionWorker(){
  try{
   string path;
   while(!exiting && motionFileQueue.TryDequeue(out path)) {
    try {
     var info=new FileInfo(path);
     if(!info.Exists || info.Length<1000)continue;
     var frames=MotionVideoProbe.Frames(ffmpeg,path);
     bool detected=false;
     foreach(var pixels in frames)if(motionDetector.Feed(pixels))detected=true;
     DateTime time=DateTime.Now;
     bool open=false,alert=false;
     lock(motionGuard){
      motionEvaluatedClips++;
      if(detected){
       motionPositiveClips++;
       motionWindow.Trigger(time,out open,out alert);
       if(open)motionTestMode=false;
      }
      if(open)StartMotionEvent(time,false,path);
      if(motionWindow.Active)ArchiveMotionClip(path);
      if(motionWindow.ShouldClose(time))FinishMotionEvent();
      motionLastCheck=time;
     }
     if(detected){
       Log("움직임 감지: "+Path.GetFileName(path)+
           " (변화 영역 "+(motionDetector.LastRatio*100).ToString("0.0")+"%, 누적 감지 "+motionPositiveClips+"회)");
       if(alert)NotifyMotion(false);
     }
    }catch(Exception e){Log("움직임 분석 오류 (원본 녹화 유지): "+Limit(e.Message,125));}
   }
  }finally{
   Interlocked.Exchange(ref motionWorkerActive,0);
   if(!motionFileQueue.IsEmpty && !exiting && Interlocked.CompareExchange(ref motionWorkerActive,1,0)==0)
    Task.Run((Action)RunMotionWorker);
  }
 }
 void StartMotionEvent(DateTime at,bool test,string triggerPath){
   string eventId=at.ToString("yyyyMMdd_HHmmss")+(test?"_TEST":"");
   motionFolder=Path.Combine(recordRoot,"events",at.ToString("yyyy-MM-dd"),eventId);
   Directory.CreateDirectory(motionFolder);
   if(motionOnly)File.WriteAllText(Path.Combine(motionFolder,"upload-event.marker"),"motion-only");
   motionEventClips=0;
   File.WriteAllText(Path.Combine(motionFolder,"event-info.txt"),
      "Event start="+at.ToString("o")+"\r\nBefore=30s\r\nAfter last motion=120s\r\nAlert cooldown=1s\r\n"+
      "Sensitivity=High\r\nPerson-only=False\r\nManualTest="+test+"\r\n"+
      "Source video remains in raw folder and is uploaded independently to MYBOX.\r\n",Encoding.UTF8);
   var baseline=at.AddSeconds(-30);
   string raw=Path.Combine(recordRoot,motionOnly?"raw-motion":"raw");
   var candidates=Directory.GetFiles(raw,"*.ts",SearchOption.AllDirectories)
     .Select(s=>new FileInfo(s))
     .Where(f=>f.LastWriteTime>=baseline&&f.LastWriteTime<=at.AddSeconds(2)&&f.Length>1000)
     .OrderBy(f=>f.LastWriteTime).ToList();
   foreach(var clip in candidates)ArchiveMotionClip(clip.FullName);
   Log("이벤트 시작: 감지 전 30초 포함, "+motionFolder+" · 복사한 파일 "+motionEventClips+"개");
 }
 void ArchiveMotionClip(string path){
   if(string.IsNullOrEmpty(motionFolder) || !File.Exists(path))return;
   try{
    var f=new FileInfo(path);
    if(f.Length<1000)return;
    string dst=Path.Combine(motionFolder,f.Name);
    if(File.Exists(dst))return;
    File.Copy(path,dst,false);
    if(new FileInfo(dst).Length!=f.Length)throw new IOException("Archived video size differs");
    motionEventClips++;
   }catch(Exception ex){
    motionArchiveFailures++;
    Log("이벤트 영상 복사 실패 (원본 보존): "+Path.GetFileName(path)+" / "+Limit(ex.Message,100));
   }
 }
 void FinishMotionEvent(){
   if(!motionWindow.Active)return;
   string path=motionFolder;
   DateTime ended=DateTime.Now;
   try{if(!string.IsNullOrEmpty(path))
      File.AppendAllText(Path.Combine(path,"event-info.txt"),
        "Finished="+ended.ToString("o")+"\r\nArchivedVideoSegments="+motionEventClips+
        "\r\nArchiveCopyErrors="+motionArchiveFailures+"\r\n",Encoding.UTF8);
   }catch(Exception ex){Log("이벤트 기록 저장 오류: "+Limit(ex.Message,100));}
   motionWindow.Close();motionFolder="";
   Log("움직임 이벤트 종료. PC 이벤트 영상 보관: "+path);
   if(!string.IsNullOrEmpty(path))Task.Run(()=>BuildEventMovie(path));
 }
 void BuildEventMovie(string folder){
  try {
   var clips=Directory.GetFiles(folder,"*.ts",SearchOption.TopDirectoryOnly).Where(s=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(s), @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.ts$"))
     .Select(s=>new FileInfo(s)).OrderBy(f=>f.Name).ToList();
   if(clips.Count==0)return;
   string output=Path.Combine(folder,"event-video.ts");
   if(!File.Exists(output)){
    CloudBundler.Pack(ffmpeg,clips,output);
    if(new FileInfo(output).Length<1000)throw new IOException("Empty merged event clip");
    if(File.Exists(Path.Combine(folder,"upload-event.marker")))QueueFresh(output);
    Log("이벤트 통합 영상 완성: "+output+" ("+clips.Count+"개 구간, 1초 원본 유지)");
   }
  }catch(Exception ex){Log("이벤트 통합 실패, 1초 원본 보존: "+Limit(ex.Message,140));}
 }
 void NotifyMotion(bool test){
   try{BeginInvoke((Action)delegate {
     if(motionTray==null)return;
     motionTray.BalloonTipTitle=test?"USB 카메라 알림 시험":"USB 카메라 움직임 감지";
     motionTray.BalloonTipText=test?"시험 감지가 작동했고 영상 이벤트를 기록했습니다.":
       "고정된 카메라 화면에서 변화가 감지됐습니다. MYBOX 백업은 계속됩니다.";
     motionTray.ShowBalloonTip(6000);
     UpdateMotionStatus();
   });}catch{}
 }
 void ForceMotionTest(){
  if(!recording){MessageBox.Show("녹화가 실행 중일 때 시험할 수 있습니다.");return;}
  if(motionOnly){
   if(!motionEventActive)StartMotionEventSession();
   else {motionEndsAt=DateTime.Now.AddSeconds(120);bool testFresh,testAlert;motionWindow.Trigger(DateTime.Now,out testFresh,out testAlert);}
   motionTestMode=true;
   Log("사용자 시험 감지: 실제 움직임 감지와 구분하며, 120초 이벤트 영상 저장·업로드를 시험합니다.");
   NotifyMotion(true);UpdateMotionStatus();return;
  }
  bool opening,alert;
  lock(motionGuard){
   DateTime now=DateTime.Now;
   motionWindow.Trigger(now,out opening,out alert);
   if(opening){motionTestMode=true;StartMotionEvent(now,true,null);}
  }
  Log("사용자 동작 시험: 이벤트 영상 사전 30초 복사 완료, 120초 연장 녹화, 클라우드 원본 업로드 유지");
  NotifyMotion(true);
  UpdateMotionStatus();
 }
 void MotionRuntimeTick(){
  if(motionStatusLabel!=null && !motionStatusLabel.IsDisposed)UpdateMotionStatus();
  lock(motionGuard) {
   if(motionWindow.Active && motionWindow.ShouldClose(DateTime.Now))FinishMotionEvent();
  }
 }
 void StopMotionRuntime(){
  lock(motionGuard){if(motionWindow.Active)FinishMotionEvent();}
  try{if(motionTray!=null){motionTray.Visible=false;motionTray.Dispose();}}catch{}
 }
}

