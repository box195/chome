using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

internal sealed class FixedCameraMotion {
 public const int Width=96,Height=54;
 public const int FrameBytes=Width*Height;
 byte[] previous;
 double lastRatio;
 public double LastRatio {get{return lastRatio;}}
 public bool Feed(byte[] frame) {
  if(frame==null || frame.Length!=FrameBytes)throw new ArgumentException("Invalid grayscale frame");
  if(previous==null){previous=(byte[])frame.Clone();lastRatio=0;return false;}
  long sum=0;
  for(int i=0;i<FrameBytes;i++)sum+=(int)frame[i]-(int)previous[i];
  double brightnessShift=(double)sum/FrameBytes;
  int changed=0; 
  for(int i=0;i<FrameBytes;i++){
   double difference=Math.Abs((int)frame[i]-(int)previous[i]-brightnessShift);
   if(difference>=29)changed++;
  }
  lastRatio=(double)changed/FrameBytes;
  previous=(byte[])frame.Clone();
  // Reject global exposure changes or frame glitches instead of generating an alarm.
  return lastRatio>=0.014 && lastRatio<=0.65;
 }
 public void Reset(){previous=null;lastRatio=0;}
 public static bool SelfTest(out string result){
  var detector=new FixedCameraMotion();
  byte[] baseline=Enumerable.Repeat((byte)80,FrameBytes).ToArray();
  byte[] bright=Enumerable.Repeat((byte)105,FrameBytes).ToArray();
  byte[] moving=(byte[])bright.Clone();
  for(int y=8;y<24;y++)for(int x=10;x<28;x++)moving[y*Width+x]=220;
  bool a=detector.Feed(baseline);
  bool b=detector.Feed(bright);
  bool c=detector.Feed(moving);
  bool d=detector.Feed(bright);
  bool ok=!a&&!b&&c&&d;
  result="baseline_false="+(!a)+" exposure_false="+(!b)+" moving_true="+c+" restore_true="+d+" ratio="+detector.LastRatio.ToString("0.000");
  return ok;
 }
}
internal sealed class MotionWindow {
 public readonly TimeSpan Pre=TimeSpan.FromSeconds(30),Post=TimeSpan.FromSeconds(120),Cooldown=TimeSpan.FromSeconds(1);
 public bool Active {get;private set;}
 public DateTime Start {get;private set;}
 public DateTime LastMotion {get;private set;}
 public DateTime LastAlert {get;private set;}
 public int Triggers {get;private set;}
 public bool Trigger(DateTime at,out bool newEvent,out bool alert) {
  newEvent=!Active;
  if(newEvent){Active=true;Start=at;Triggers=0;}
  Triggers++;
  LastMotion=at;
  alert=LastAlert==DateTime.MinValue || (at-LastAlert)>=Cooldown;
  if(alert)LastAlert=at;
  return true;
 }
 public bool ShouldClose(DateTime at){return Active && (at-LastMotion)>Post;}
 public void Close(){Active=false;}
 public static bool SelfTest(out string result) {
  var s=new MotionWindow();
  var t=new DateTime(2026,10,20,19,0,0);
  bool created,alert;
  s.Trigger(t,out created,out alert);
  bool a=created&&alert;
  s.Trigger(t.AddMilliseconds(500),out created,out alert);
  bool b=!created&&!alert&&!s.ShouldClose(t.AddSeconds(120));
  s.Trigger(t.AddSeconds(1),out created,out alert);
  bool c=!created&&alert&&!s.ShouldClose(t.AddSeconds(120))&&s.ShouldClose(t.AddSeconds(122));
  s.Close();
  result="new_event="+a+" cooldown="+b+" extend_120s="+c+" closed="+(!s.Active);
  return a&&b&&c&&!s.Active;
 }
}
internal static class MotionVideoProbe {
 public static List<byte[]> Frames(string ffmpeg,string file){
  var p=new Process();
  p.StartInfo=new ProcessStartInfo(ffmpeg,
   "-nostdin -hide_banner -loglevel error -i "+Quote(file)+
   " -vf "+Quote("fps=2,scale=96:54:flags=fast_bilinear,format=gray")+
   " -an -f rawvideo -pix_fmt gray -")
   {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  using(p){
   p.Start();
   byte[] bytes;using(var ms=new MemoryStream()) {p.StandardOutput.BaseStream.CopyTo(ms);bytes=ms.ToArray();}
   if(!p.WaitForExit(8000)){try{p.Kill();}catch{}throw new IOException("motion decode timeout");}
   string err=p.StandardError.ReadToEnd();
   if(p.ExitCode!=0)throw new IOException("motion video error: "+(err.Length>130?err.Substring(0,130):err));
   var frames=new List<byte[]>();
   for(int i=0;i+FixedCameraMotion.FrameBytes<=bytes.Length;i+=FixedCameraMotion.FrameBytes){
    var sample=new byte[FixedCameraMotion.FrameBytes];
    Buffer.BlockCopy(bytes,i,sample,0,sample.Length);
    frames.Add(sample);
   }
   return frames;
  }
 }
 static string Quote(string p){return "\""+p.Replace("\"","\\\"")+"\"";}
}

