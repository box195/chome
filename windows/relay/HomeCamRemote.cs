using System;
using System.IO;
using System.Text;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Threading;

class HomeCamRemote {
 static string baseDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"USBHomeCamNAVER","remote");
 static string pinFile=Path.Combine(baseDir,"pair-code.txt");
 static string statusFile=Path.Combine(baseDir,"state.json");
 static string commandDir=Path.Combine(baseDir,"commands");
 static string indexFile=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"index.html");
 static string desktopExe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"USBHomeCam-NAVER-Controlled.exe");
 static readonly Dictionary<string,DateTime> sessions=new Dictionary<string,DateTime>();
 static readonly JavaScriptSerializer json=new JavaScriptSerializer();
 static readonly object authLock=new object();
 static readonly Queue<DateTime> failedPairs=new Queue<DateTime>();
 static string pin="";
 static int Main(string[] args){
  if(args.Length==1 && args[0]=="--self-test")return CloudProtocol.RunTests();
  try{
   Directory.CreateDirectory(baseDir);Directory.CreateDirectory(commandDir);
   if(File.Exists(pinFile) && File.ReadAllText(pinFile).Trim().Length==10){pin=File.ReadAllText(pinFile).Trim();}
   else{
    var bytes=new byte[5];using(var rng=RandomNumberGenerator.Create()){rng.GetBytes(bytes);}
    pin=BitConverter.ToString(bytes).Replace("-","").Substring(0,10);
    File.WriteAllText(pinFile,pin,Encoding.ASCII);
   }
   var listener=new HttpListener();
   listener.Prefixes.Add("http://127.0.0.1:18742/"); listener.Prefixes.Add("http://localhost:18742/");
   listener.Start(); CHomeCloud.Start();
   Console.WriteLine("REMOTECAM_READY port=18742 pairCodeStoredLocally=true");
   while(true){
    var ctx=listener.GetContext();
    ThreadPool.QueueUserWorkItem(o=>{try{Handle((HttpListenerContext)o);}catch(Exception e){Console.Error.WriteLine("REQUEST_ERROR "+e.GetType().Name);}},ctx);
   }
  }catch(Exception e){Console.Error.WriteLine("FATAL "+e.GetType().Name+": "+e.Message);return 2;}
 }
 static void Handle(HttpListenerContext ctx){
  var req=ctx.Request;
  var res=ctx.Response;
  res.Headers["Cache-Control"]="no-store";
  res.Headers["X-Content-Type-Options"]="nosniff";
  res.Headers["Referrer-Policy"]="no-referrer";
  res.Headers["X-Frame-Options"]="DENY";
  res.Headers["Content-Security-Policy"]="default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' https://www.gstatic.com https://apis.google.com https://*.firebaseio.com; frame-src 'self' https://chome-a8970.firebaseapp.com https://accounts.google.com; connect-src 'self' wss://*.firebaseio.com https://*.firebaseio.com https://chome-a8970.firebaseapp.com https://www.googleapis.com https://box1.tail23cca9.ts.net:8443 https://chome-a8970-default-rtdb.firebaseio.com https://identitytoolkit.googleapis.com https://securetoken.googleapis.com; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'";
  string origin=req.Headers["Origin"];
  bool cross=origin=="https://box195.github.io";
  if(cross){
   res.Headers["Access-Control-Allow-Origin"]=origin;
   res.Headers["Vary"]="Origin";
   res.Headers["Access-Control-Allow-Headers"]="Content-Type,Authorization,X-HomeCam-Token";
   res.Headers["Access-Control-Allow-Methods"]="GET,POST,OPTIONS";
   res.Headers["Access-Control-Allow-Private-Network"]="true";
  }
  bool trustedSameOrigin = origin == "http://localhost:18742" || origin == "http://127.0.0.1:18742" || origin == "https://box1.tail23cca9.ts.net:8443"; if(!string.IsNullOrEmpty(origin) && !cross && !trustedSameOrigin) { WriteJson(res,403,new{error="origin_not_allowed"});return; }
  if(req.HttpMethod=="OPTIONS"){Write(res,204,"","text/plain");return;}
  if(CHomeCloud.TryHandle(ctx,pin))return;
  string path=req.Url.AbsolutePath;
  if(req.HttpMethod=="GET" && (path=="/" || path=="/index.html")){
   if(!File.Exists(indexFile)){Write(res,500,"dashboard missing");return;}
   Write(res,200,File.ReadAllText(indexFile,Encoding.UTF8),"text/html; charset=utf-8");return;
  }
  if(req.HttpMethod=="GET" && path=="/app.js"){Write(res,200,File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"app.js"),Encoding.UTF8),"application/javascript; charset=utf-8");return;}
  if(req.HttpMethod=="GET" && path=="/chome.html"){Write(res,200,File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"chome.html"),Encoding.UTF8),"text/html; charset=utf-8");return;}
  if(req.HttpMethod=="GET" && path=="/chome.js"){Write(res,200,File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"chome.bundle.js"),Encoding.UTF8),"application/javascript; charset=utf-8");return;}
  if(req.HttpMethod=="GET" && path=="/health"){WriteJson(res,200,new{service=true,ready=true});return;}
  if(req.HttpMethod=="POST" && path=="/api/pair"){
   string code="";
   try{
    if(req.ContentLength64>128){WriteJson(res,413,new{error="size"});return;}
    using(var read=new StreamReader(req.InputStream,Encoding.UTF8)){
     var data=read.ReadToEnd();var obj=json.Deserialize<Dictionary<string,object>>(data);
     code=obj.ContainsKey("code")?Convert.ToString(obj["code"]):"";
    }
   }catch{WriteJson(res,400,new{error="invalid"});return;}
   lock(authLock){
    while(failedPairs.Count>0 && (DateTime.UtcNow-failedPairs.Peek()).TotalMinutes>15)failedPairs.Dequeue();
    if(failedPairs.Count>=8){WriteJson(res,429,new{error="temporarily_blocked"});return;}
    byte[] a=Encoding.UTF8.GetBytes(code),b=Encoding.UTF8.GetBytes(pin);
    bool equal=a.Length==b.Length;
    int diff=a.Length^b.Length;
    for(int i=0;i<Math.Min(a.Length,b.Length);i++)diff|=a[i]^b[i];
    if(!equal || diff!=0){failedPairs.Enqueue(DateTime.UtcNow);WriteJson(res,401,new{error="invalid_code"});return;}
    var rand=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(rand);
    string token=BitConverter.ToString(rand).Replace("-","");
    sessions[token]=DateTime.UtcNow.AddDays(30);
    WriteJson(res,200,new{token=token,expiresDays=30});return;
   }
  }
  var auth=req.Headers["X-HomeCam-Token"];
  bool valid=false;
  lock(authLock){
   DateTime end;
   if(!string.IsNullOrWhiteSpace(auth) && sessions.TryGetValue(auth,out end) && end>DateTime.UtcNow)valid=true;
  }
  if(!valid){WriteJson(res,401,new{error="pair_required"});return;}
  if(req.HttpMethod=="GET" && path=="/api/status"){
   bool appRunning=Process.GetProcessesByName("USBHomeCam-NAVER-Controlled").Any(p=>{try{return !p.HasExited && p.HandleCount>0;}catch{return false;}});
   Dictionary<string,object> state=null;
   try{
    if(File.Exists(statusFile))state=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(statusFile,Encoding.UTF8));
   }catch{}
   DateTime updated=DateTime.MinValue;
   DateTimeOffset parsed; if(state!=null && state.ContainsKey("updatedAt") && DateTimeOffset.TryParse(Convert.ToString(state["updatedAt"]),out parsed))updated=parsed.UtcDateTime;
   bool live=appRunning && updated>DateTime.UtcNow.AddSeconds(-12);
   if(state==null)state=new Dictionary<string,object>();
   state["online"]=live;
   state["appProcess"]=appRunning;
   state["checkedAt"]=DateTime.UtcNow.ToString("o");
   if(!live){state["recording"]=false;state["cameraConnected"]=false;}
   WriteJson(res,200,state);return;
  }
  if(req.HttpMethod=="POST" && (path=="/api/start" || path=="/api/stop")){
   string action=path=="/api/start"?"START":"STOP";
   if(!File.Exists(desktopExe)){WriteJson(res,503,new{error="windows_recorder_missing"});return;}
   if(!Process.GetProcessesByName("USBHomeCam-NAVER-Controlled").Any(p=>{try{return !p.HasExited && p.HandleCount>0;}catch{return false;}})){
    try{Process.Start(new ProcessStartInfo(desktopExe){UseShellExecute=true});}
    catch{WriteJson(res,503,new{error="could_not_start_recorder"});return;}
   }
   string name=DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"_"+Guid.NewGuid().ToString("N")+".cmd";
   File.WriteAllText(Path.Combine(commandDir,name),action,Encoding.ASCII);
   WriteJson(res,202,new{accepted=true,command=action,queueTime=DateTime.UtcNow.ToString("o")});return;
  }
  WriteJson(res,404,new{error="not_found"});
 }
 static void WriteJson(HttpListenerResponse r,int code,object data){Write(r,code,json.Serialize(data),"application/json; charset=utf-8");}
 static void Write(HttpListenerResponse r,int code,string body,string type="text/plain; charset=utf-8"){
  try{
   byte[] bytes=Encoding.UTF8.GetBytes(body);
   r.StatusCode=code;r.ContentType=type;
   r.ContentLength64=bytes.Length;
   r.OutputStream.Write(bytes,0,bytes.Length);
  }catch{}finally{try{r.OutputStream.Close();}catch{}}
 }
}

