using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;

internal static class CloudBundler {
 static string Quote(string s) { return "\""+s.Replace("\"","\\\"")+"\""; }
 internal static string Pack(string ffmpeg,List<FileInfo> clips,string output){
  if(clips==null || clips.Count==0)throw new ArgumentException("No video clips");
  Directory.CreateDirectory(Path.GetDirectoryName(output));
  if(clips.Count==1){File.Copy(clips[0].FullName,output,true);return output;}
  string list=output+".concat.txt";
  try {
   string s=string.Join(Environment.NewLine,clips.Select(c=>"file '"+c.FullName.Replace("'","'\\''")+"'"))+Environment.NewLine;
   File.WriteAllText(list,s,new UTF8Encoding(false));
   using(var p=new Process()){
    p.StartInfo=new ProcessStartInfo(ffmpeg,"-nostdin -hide_banner -loglevel error -y -f concat -safe 0 -i "+Quote(list)+" -map 0:v:0 -c copy -f mpegts "+Quote(output)){
      UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true
    };
    p.Start();
    string err=p.StandardError.ReadToEnd();
    if(!p.WaitForExit(30000)){try{p.Kill();}catch{}throw new IOException("묶음 생성 시간 초과");}
    if(p.ExitCode!=0)throw new IOException("묶음 생성 실패: "+(err.Length>120?err.Substring(0,120):err));
   }
   var result=new FileInfo(output);
   if(!result.Exists || result.Length<1000)throw new IOException("묶음 영상이 비어 있습니다.");
   return output;
  } finally {try{File.Delete(list);}catch{}}
 }
}

