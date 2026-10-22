using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;

internal sealed partial class CamForm {
 bool motionOnly=false;
 bool motionEventActive=false;
 DateTime motionEndsAt=DateTime.MinValue;
 DateTime lastMotionProbeStart=DateTime.MinValue;
 Process motionProbe;
 int motionTriggerPending=0;
 readonly object motionProbeGuard=new object();
 ComboBox pcRecordingModeBox;

 void LoadRecordingMode(){
  try{motionOnly=File.ReadAllText(Path.Combine(remotePath,"recording-mode.txt")).Trim()=="MOTION";}
  catch{motionOnly=false;}
 }
 void SetRecordingMode(bool selected){
  if(motionOnly==selected)return;
  bool wasActive=recording;
  if(wasActive)StopRecording();
  motionOnly=selected;
  try{
   Directory.CreateDirectory(remotePath);
   File.WriteAllText(Path.Combine(remotePath,"recording-mode.txt"),selected?"MOTION":"CONTINUOUS");
  }catch(Exception e){Log("Recording mode save failed: "+Limit(e.Message,90));}
  if(pcRecordingModeBox!=null){
   int index=motionOnly?1:0;
   if(pcRecordingModeBox.SelectedIndex!=index)pcRecordingModeBox.SelectedIndex=index;
  }
  if(wasActive)StartRecording();
 }
 void SetupRecordingModeUI(FlowLayoutPanel panel){
  panel.Controls.Add(new Label{Text="PC mode",Width=65,Height=26,TextAlign=ContentAlignment.MiddleLeft});
  pcRecordingModeBox=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=180};
  pcRecordingModeBox.Items.AddRange(new object[]{"Continuous","Motion-only: 1s x 120s"});
  pcRecordingModeBox.SelectedIndex=motionOnly?1:0;
  pcRecordingModeBox.SelectedIndexChanged+=delegate{SetRecordingMode(pcRecordingModeBox.SelectedIndex==1);};
  panel.Controls.Add(pcRecordingModeBox);
 }
 // The detector never creates video files. Only a single replaceable JPEG
 // supports on-demand live preview when waiting for movement.
 void StartMotionProbe(){
  if(!recording||!motionOnly||motionEventActive||exiting||string.IsNullOrEmpty(cameraName))return;
  lock(motionProbeGuard){
   if(motionProbe!=null){try{if(!motionProbe.HasExited)return;}catch{}}
   if((DateTime.Now-lastMotionProbeStart).TotalSeconds<5)return;
   lastMotionProbeStart=DateTime.Now;
   var p=new Process();
   string arg="-hide_banner -loglevel warning -f dshow -i "+Q("video="+cameraName)+
     " -an -vf "+Q("fps=6,scale=320:180:flags=fast_bilinear")+
     " -f rawvideo -pix_fmt bgr24 -";
   p.StartInfo=new ProcessStartInfo(ffmpeg,arg){
     UseShellExecute=false,CreateNoWindow=true,
     RedirectStandardOutput=true,RedirectStandardError=true};
   try{
    if(!p.Start())throw new IOException("camera probe not started");
    motionProbe=p;
    p.BeginErrorReadLine();
    Task.Run(()=>RunMotionProbe(p));
    Log("Motion mode: detection only; not recording any video");
   }catch(Exception e){Log("Motion camera probe error: "+Limit(e.Message,90));try{p.Dispose();}catch{}}
  }
 }
 void StopMotionProbe(){
  lock(motionProbeGuard){
   var p=motionProbe;motionProbe=null;
   if(p!=null){try{if(!p.HasExited){p.Kill();p.WaitForExit(650);}}catch{}}
  }
 }
 void RunMotionProbe(Process p){
  var detector=new FixedCameraMotion();
  const int w=320,h=180,n=w*h*3;
  var bytes=new byte[n];
  int index=0;
  try{
   var stream=p.StandardOutput.BaseStream;
   while(recording && motionOnly && !motionEventActive && !exiting){
    int got=0;
    while(got<n){
     int m=stream.Read(bytes,got,n-got);
     if(m<=0)break;
     got+=m;
    }
    if(got!=n)break;
    // A single image in the private application directory is a preview, not a clip.
    WriteMotionPreviewJpeg(bytes,w,h);
    var gray=new byte[FixedCameraMotion.FrameBytes];
    for(int y=0;y<FixedCameraMotion.Height;y++)
     for(int x=0;x<FixedCameraMotion.Width;x++){
      int yy=y*h/FixedCameraMotion.Height,xx=x*w/FixedCameraMotion.Width;
      int offset=(yy*w+xx)*3;
      gray[y*FixedCameraMotion.Width+x]=(byte)((bytes[offset]*11+bytes[offset+1]*59+bytes[offset+2]*30)/100);
     }
    if(detector.Feed(gray) && Interlocked.CompareExchange(ref motionTriggerPending,1,0)==0){
      try{BeginInvoke((Action)delegate {
        try{StartMotionEventSession();}finally{Interlocked.Exchange(ref motionTriggerPending,0);}
      });}catch{Interlocked.Exchange(ref motionTriggerPending,0);}
    }
   }
  }catch(Exception e){if(!exiting && !motionEventActive)Log("Camera probe ended: "+Limit(e.Message,90));}
  finally{try{p.Dispose();}catch{}}
 }
 void WriteMotionPreviewJpeg(byte[] raw,int w,int h){
  try{
   Directory.CreateDirectory(remotePath);
   string target=Path.Combine(remotePath,"preview.jpg"),temp=target+".new";
   using(var bitmap=new Bitmap(w,h,PixelFormat.Format24bppRgb)){
    var rect=new Rectangle(0,0,w,h);
    var d=bitmap.LockBits(rect,ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
    try{Marshal.Copy(raw,0,d.Scan0,raw.Length);}finally{bitmap.UnlockBits(d);}
    bitmap.Save(temp,ImageFormat.Jpeg);
   }
   if(File.Exists(target))File.Replace(temp,target,null);
   else File.Move(temp,target);
  }catch{}
 }
 void StartMotionEventSession(){
  if(!recording||!motionOnly||motionEventActive||exiting)return;
  motionEventActive=true;
  motionEndsAt=DateTime.Now.AddSeconds(120);
  bool fresh,alert;
  motionWindow.Trigger(DateTime.Now,out fresh,out alert);
  motionPositiveClips++;
  StopMotionProbe();
  if(alert)NotifyMotion(false);
  Log("Motion detected: starting 120s video session, upload each closed 1s segment");
  StartCapture();
 }
 void TickMotionSession(){
  if(!recording||!motionOnly)return;
  if(motionEventActive){
   if(DateTime.Now>=motionEndsAt){
    // Wait for encoder flush before uploading the last 1s chunk.
    GracefulCloseMotionCapture();
    motionEventActive=false;motionWindow.Close();
    Log("120s motion recording complete; return to detection-only");
    StartMotionProbe();
   } else if((capture==null||capture.HasExited)&&(DateTime.Now-lastCameraStart).TotalSeconds>3)
    StartCapture();
  }else StartMotionProbe();
 }
 void GracefulCloseMotionCapture(){
  var p=capture;
  if(p!=null){
   try{if(!p.HasExited){
    p.StandardInput.WriteLine("q");p.StandardInput.Flush();
    if(!p.WaitForExit(2300))p.Kill();
   }}catch{try{if(!p.HasExited)p.Kill();}catch{}}
  }
  capture=null;
  string last;lock(segmentSync){last=activeSegmentPath;activeSegmentPath="";}
  if(!string.IsNullOrEmpty(last) &&
     last.Contains(Path.DirectorySeparatorChar+"motion-chunks"+Path.DirectorySeparatorChar) &&
     File.Exists(last) && new FileInfo(last).Length>1000)QueueFresh(last);
 }
 // Batching is exclusively for continuous source chunks. Never batch motion chunks.
 bool TryUploadContinuousBundle(string first,string token){
  var initial=new FileInfo(first);
  if(!initial.Exists||File.Exists(Marker(initial)))return initial.Exists;
  string current;lock(segmentSync){current=activeSegmentPath;}
  var options=Directory.GetFiles(initial.DirectoryName,"*.ts")
    .Select(path=>new FileInfo(path))
    .Where(f=>f.Length>1000&&!File.Exists(Marker(f))&&
       !string.Equals(f.FullName,current,StringComparison.OrdinalIgnoreCase))
    .OrderBy(f=>f.Name).Take(2).ToList();
  if(options.Count==0)return false;
  if(options.Count<2 && DateTime.UtcNow-options[0].LastWriteTimeUtc<TimeSpan.FromMinutes(2))
   return false;
  string stage=Path.Combine(data,"staging",Path.GetFileNameWithoutExtension(options[0].Name)+"_bundle"+options.Count+".ts");
  Directory.CreateDirectory(Path.GetDirectoryName(stage));
  try{
   CloudBundler.Pack(ffmpeg,options,stage);
   MyboxApi.Upload(token,myboxFolderId,stage);
   foreach(var f in options){
    var marker=Marker(f);Directory.CreateDirectory(Path.GetDirectoryName(marker));
    File.WriteAllText(marker,Identity(f),Encoding.UTF8);
   }
   completedUploads++;lastUpload=Path.GetFileName(stage);
   return true;
  }finally{try{File.Delete(stage);}catch{}}
 }
}