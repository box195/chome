using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

static class CHomeCloud {
 const string ApiKey="AIzaSyBeA0nsSOJ9HbV7ZLt4XAcgYX6uWfP7ANo";
 const string Root="https://chome-a8970-default-rtdb.firebaseio.com";
 static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"USBHomeCamNAVER","remote");
 static readonly string CloudKey=Path.Combine(Folder,"cloud-credential.dat");
 static readonly string UidFile=Path.Combine(Folder,"cloud-uid.txt");
 static readonly string LastFile=Path.Combine(Folder,"cloud-v2-last-seq.txt");
 static readonly string PendingFile=Path.Combine(Folder,"cloud-v2-pending.json");
 static readonly string OwnerFile=Path.Combine(Folder,"owner-uid.txt");
 static readonly string CloudQueue=Path.Combine(Folder,"cloud-commands");
 static readonly string CloudResults=Path.Combine(Folder,"cloud-results");
 static CloudCommand Pending;
 static readonly string LocalState=Path.Combine(Folder,"state.json");
 static readonly string Commands=Path.Combine(Folder,"commands");
 static readonly string DesktopExe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"USBHomeCam-NAVER-Controlled.exe");
 static readonly JavaScriptSerializer J=new JavaScriptSerializer();
 static readonly byte[] Entropy=Encoding.UTF8.GetBytes("CHome-Cloud-v1-Windows-user");
 static readonly object Sync=new object();
 static readonly Queue<DateTime> BadPairTimes=new Queue<DateTime>();
 static readonly object Io=new object();
 static string Uid,RefreshToken,IdToken;
 static DateTime TokenExpiry=DateTime.MinValue;
 static long LastSeq;
 static volatile bool Working=true;
 public static void Start(){
  Directory.CreateDirectory(Folder); Directory.CreateDirectory(Commands); Directory.CreateDirectory(CloudQueue); Directory.CreateDirectory(CloudResults);
  try{if(File.Exists(CloudKey)){RefreshToken=Decrypt(File.ReadAllBytes(CloudKey)); Uid=File.ReadAllText(UidFile).Trim();}}catch{RefreshToken=null;Uid=null;}
  try{long.TryParse(File.ReadAllText(LastFile),out LastSeq);}catch{}
  try{
   if(File.Exists(PendingFile)) {
    var saved=Parse(File.ReadAllText(PendingFile,Encoding.UTF8));
    long seq,issued;
    if(long.TryParse(GetValue(saved,"seq"),out seq) && long.TryParse(GetValue(saved,"issuedAt"),out issued)){
     Pending=new CloudCommand{Seq=seq,Id=GetValue(saved,"id"),Action=GetValue(saved,"action"),Mode=GetValue(saved,"mode"),IssuedAt=issued};
    }
   }
  }catch{Pending=null;}
  var t=new Thread(Loop){IsBackground=true,Name="CHome-Firebase-Relay"};t.Start();
 }
 static string GetValue(Dictionary<string,object> obj,string key){
  return obj!=null && obj.ContainsKey(key)?Convert.ToString(obj[key]):"";
 }
 static Dictionary<string,object> Parse(string raw){return J.Deserialize<Dictionary<string,object>>(raw);}
 static string Decrypt(byte[] ciphertext){return Encoding.UTF8.GetString(ProtectedData.Unprotect(ciphertext,Entropy,DataProtectionScope.CurrentUser));}
 static byte[] Encrypt(string value){return ProtectedData.Protect(Encoding.UTF8.GetBytes(value),Entropy,DataProtectionScope.CurrentUser);}
 static string PostForm(string url,Dictionary<string,string> data){
  var sb=new StringBuilder();
  foreach(var kv in data){
   if(sb.Length>0)sb.Append("&");
   sb.Append(Uri.EscapeDataString(kv.Key)).Append("=").Append(Uri.EscapeDataString(kv.Value));
  }
  byte[] bytes=Encoding.UTF8.GetBytes(sb.ToString());
  var req=(HttpWebRequest)WebRequest.Create(url);
  req.Method="POST"; req.ContentType="application/x-www-form-urlencoded";req.Timeout=8500; req.ReadWriteTimeout=8500;
  using(var st=req.GetRequestStream())st.Write(bytes,0,bytes.Length);
  using(var resp=(HttpWebResponse)req.GetResponse())using(var sr=new StreamReader(resp.GetResponseStream()))return sr.ReadToEnd();
 }
 static string PostJson(string url,string json){
  var req=(HttpWebRequest)WebRequest.Create(url);
  req.Method="POST";req.ContentType="application/json";req.Timeout=8500;req.ReadWriteTimeout=8500;
  var buf=Encoding.UTF8.GetBytes(json);
  using(var st=req.GetRequestStream())st.Write(buf,0,buf.Length);
  using(var resp=(HttpWebResponse)req.GetResponse())using(var sr=new StreamReader(resp.GetResponseStream()))return sr.ReadToEnd();
 }
 static Dictionary<string,object> Exchange(string refresh){
  var res=Parse(PostForm("https://securetoken.googleapis.com/v1/token?key="+ApiKey,new Dictionary<string,string>{{"grant_type","refresh_token"},{"refresh_token",refresh}}));
  if(string.IsNullOrEmpty(GetValue(res,"id_token")))throw new Exception("Firebase token refresh failed");
  return res;
 }
 public static bool TryHandle(HttpListenerContext ctx,string pin){
  if(ctx.Request.Url.AbsolutePath!="/api/cloud/link")return false;
  var req=ctx.Request;
  if(req.HttpMethod!="POST"){Reply(ctx,405,new{error="method_not_allowed"});return true;}
  if(req.ContentLength64>8192){Reply(ctx,413,new{error="too_large"});return true;}
  Dictionary<string,object> o;
  try{using(var sr=new StreamReader(req.InputStream,Encoding.UTF8))o=Parse(sr.ReadToEnd());}
  catch{Reply(ctx,400,new{error="invalid_json"});return true;}
  string code=GetValue(o,"code").Trim().ToUpperInvariant(), supplied=pin.Trim().ToUpperInvariant();
  lock(Sync){
   while(BadPairTimes.Count>0 && DateTime.UtcNow.Subtract(BadPairTimes.Peek()).TotalMinutes>15)BadPairTimes.Dequeue();
   if(BadPairTimes.Count>=7){Reply(ctx,429,new{error="pairing_temporarily_locked"});return true;}
   byte[] a=Encoding.ASCII.GetBytes(code),b=Encoding.ASCII.GetBytes(supplied);
   int diff=a.Length^b.Length;
   for(int i=0;i<Math.Min(a.Length,b.Length);i++)diff|=a[i]^b[i];
   if(diff!=0){BadPairTimes.Enqueue(DateTime.UtcNow);Reply(ctx,401,new{error="invalid_pair_code"});return true;}
  }
  string incomingRefresh=GetValue(o,"refreshToken"),userUid=GetValue(o,"uid");
  if(incomingRefresh.Length<30 || incomingRefresh.Length>3000 || userUid.Length<8 || userUid.Length>150){
   Reply(ctx,400,new{error="bad_credentials"});return true;
  }
  try{
   var info=Exchange(incomingRefresh);
   string idToken=GetValue(info,"id_token");
   string validatedUid=GetValue(info,"user_id");
   if(validatedUid!=userUid){Reply(ctx,403,new{error="identity_mismatch"});return true;}
   // Provisioned by parent owner only, never from the public browser or first claimant.
   string approvedUid="";
   try{approvedUid=File.ReadAllText(OwnerFile,Encoding.UTF8).Trim();}catch{}
   if(string.IsNullOrWhiteSpace(approvedUid)){Reply(ctx,503,new{error="owner_not_provisioned"});return true;}
   if(validatedUid!=approvedUid){Reply(ctx,403,new{error="not_project_owner"});return true;}
   string lookup=PostJson("https://identitytoolkit.googleapis.com/v1/accounts:lookup?key="+ApiKey,J.Serialize(new{idToken=idToken}));
   var users=Parse(lookup);
   bool hasGoogle=false;
   object data;
   if(users.TryGetValue("users",out data)){
    var list=data as System.Collections.ArrayList;
    if(list!=null && list.Count>0){
     var user=list[0] as Dictionary<string,object>;
     object prov;
     if(user!=null && user.TryGetValue("providerUserInfo",out prov)){
      var providers=prov as System.Collections.ArrayList;
      if(providers!=null)foreach(var x in providers){
       var p=x as Dictionary<string,object>;
       if(p!=null && GetValue(p,"providerId")=="google.com")hasGoogle=true;
      }
     }
    }
   }
   if(!hasGoogle){Reply(ctx,403,new{error="google_account_required"});return true;}
   string rotated=GetValue(info,"refresh_token");
   if(!string.IsNullOrEmpty(rotated))incomingRefresh=rotated;
   lock(Sync){
    File.WriteAllBytes(CloudKey,Encrypt(incomingRefresh));
    File.WriteAllText(UidFile,validatedUid,Encoding.UTF8);
    Uid=validatedUid;RefreshToken=incomingRefresh;IdToken=idToken;TokenExpiry=DateTime.UtcNow.AddMinutes(50);
   }
   Reply(ctx,200,new{linked=true,uid=validatedUid,detail="PC paired to CHome Firebase"});return true;
  }catch(WebException){
   Reply(ctx,401,new{error="firebase_auth_failed"});return true;
  }catch{
   Reply(ctx,503,new{error="firebase_link_unavailable"});return true;
  }
 }
 static void Reply(HttpListenerContext ctx,int code,object body){
  var b=Encoding.UTF8.GetBytes(J.Serialize(body));
  var res=ctx.Response;
  try{res.StatusCode=code;res.ContentType="application/json; charset=utf-8";res.ContentLength64=b.Length;res.OutputStream.Write(b,0,b.Length);}
  finally{res.OutputStream.Close();}
 }
 static string Request(string method,string path,string token,string body=null){
  var url=Root+"/clients/"+Uri.EscapeDataString(Uid)+"/"+path+".json?auth="+Uri.EscapeDataString(token);
  var req=(HttpWebRequest)WebRequest.Create(url);
  req.Method=method;req.Timeout=7500;req.ReadWriteTimeout=7500;
  if(body!=null){
   req.ContentType="application/json";
   byte[] bytes=Encoding.UTF8.GetBytes(body);
   using(var s=req.GetRequestStream())s.Write(bytes,0,bytes.Length);
  }
  using(var response=(HttpWebResponse)req.GetResponse())using(var sr=new StreamReader(response.GetResponseStream()))return sr.ReadToEnd();
 }
 static long Now(){return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();}
 static void SaveLast(long seq) {
  string tmp=LastFile+".tmp";
  File.WriteAllText(tmp,seq.ToString(System.Globalization.CultureInfo.InvariantCulture),Encoding.ASCII);
  if(File.Exists(LastFile))File.Replace(tmp,LastFile,null);
  else File.Move(tmp,LastFile);
  LastSeq=seq;
 }
 static void SavePending(CloudCommand cmd) {
  File.WriteAllText(PendingFile,J.Serialize(new{id=cmd.Id,seq=cmd.Seq,action=cmd.Action,mode=cmd.Mode,issuedAt=cmd.IssuedAt}),Encoding.UTF8);
  Pending=cmd;
 }
 static void ClearPending() {
  Pending=null;
  try{if(File.Exists(PendingFile))File.Delete(PendingFile);}catch{}
 }
 static Dictionary<string,object> ReadLocalState() {
  try{
   if(File.Exists(LocalState))return Parse(File.ReadAllText(LocalState,Encoding.UTF8));
  }catch{}
  return new Dictionary<string,object>();
 }
 static bool IsRecorderRunning() {
  try{return Process.GetProcessesByName("USBHomeCam-NAVER-Controlled").Length>0;}catch{return false;}
 }
 static Dictionary<string,object> PublishableState(Dictionary<string,object> local,long now) {
  bool connected=IsRecorderRunning() && CloudProtocol.RecentState(local,now);
  var s=new Dictionary<string,object>{
   {"bridgeAt",now},{"online",connected},{"appProcess",IsRecorderRunning()},
   {"recording",connected && CloudProtocol.Field(local,"recording")=="True"},
   {"mode",connected?CloudProtocol.Field(local,"mode"):"OFF"},
   {"cameraConnected",connected && CloudProtocol.Field(local,"cameraConnected")=="True"},
   {"cloudConnected",connected && CloudProtocol.Field(local,"cloudConnected")=="True"},
   {"uploading",connected && CloudProtocol.Field(local,"uploading")=="True"},
   {"motionActive",connected && CloudProtocol.Field(local,"motionActive")=="True"},
   {"pendingUploads",connected?SafeInt(local,"pendingUploads"):0},
   {"motionDetections",connected?SafeInt(local,"motionDetections"):0},
   {"uploadedThisSession",connected?SafeInt(local,"uploadedThisSession"):0},
   {"uploadFailures",connected?SafeInt(local,"uploadFailures"):0},
   {"lastMotion",connected?CloudProtocol.Field(local,"lastMotion"):""},
   {"updatedAt",connected?CloudProtocol.Field(local,"updatedAt"):""}
  };
  return s;
 }
 static int SafeInt(Dictionary<string,object> d,string key) {
  int v; return int.TryParse(CloudProtocol.Field(d,key),out v)?Math.Max(0,v):0;
 }
 static void Ack(string token,CloudCommand c,string phase,string detail,long now) {
  Request("PUT","ack",token,J.Serialize(new {id=c.Id,seq=c.Seq,action=c.Action,phase=phase,detail=detail,updatedAt=now}));
 }
 static void ProcessPending(string token,Dictionary<string,object> local,long now) {
  if(Pending==null)return;
  var c=Pending;
  var resultFile=Path.Combine(CloudResults,c.Id+".json");
  Dictionary<string,object> receipt=null;
  try{if(File.Exists(resultFile))receipt=Parse(File.ReadAllText(resultFile,Encoding.UTF8));}catch{}
  if(CloudProtocol.MatchesReceipt(receipt,c) && CloudProtocol.ResultApplied(c,receipt,local,now)){
   Ack(token,c,"applied","recorder_state_confirmed",now);
   try{File.Delete(resultFile);}catch{}
   ClearPending();return;
  }
  if(receipt!=null && CloudProtocol.Field(receipt,"phase")=="failed" &&
     CloudProtocol.Field(receipt,"id")==c.Id && CloudProtocol.Field(receipt,"action")==c.Action && CloudProtocol.Field(receipt,"seq")==c.Seq.ToString()){
   Ack(token,c,"failed","recorder_rejected",now);
   try{File.Delete(resultFile);}catch{}
   ClearPending();return;
  }
  if(now-c.IssuedAt>CloudProtocol.ResultTimeoutMs) {
   Ack(token,c,"failed","recorder_confirmation_timeout",now);
   ClearPending();return;
  }
  // A queued acknowledgement is never success; only a verified recorder state is applied.
 }
 static void HandleCloudCommand(string token,string uid,string raw,long now) {
  if(string.IsNullOrWhiteSpace(raw)||raw=="null")return;
  Dictionary<string,object> obj;
  try{obj=Parse(raw);}catch{return;}
  long seq;
  if(!long.TryParse(CloudProtocol.Field(obj,"seq"),out seq)||seq<=LastSeq)return;
  CloudCommand c;string why;
  if(!CloudProtocol.Parse(obj,uid,LastSeq,now,out c,out why)){
   SaveLast(seq);
   // Never execute stale/unknown actions.
   return;
  }
  if(Pending!=null) {
   SaveLast(seq);Ack(token,c,"failed","another_command_pending",now);return;
  }
  // Persist before dispatch. A crash errs on non-replay, never duplicate execution.
  SaveLast(seq);SavePending(c);
  string dest=Path.Combine(CloudQueue,c.Seq.ToString("D20")+"_"+c.Id+".cmd");
  string tmp=dest+".part";
  File.WriteAllText(tmp,J.Serialize(new{id=c.Id,seq=c.Seq,action=c.Action,mode=c.Mode,issuedAt=c.IssuedAt}),Encoding.UTF8);
  File.Move(tmp,dest);
  if(c.Action=="START" && !IsRecorderRunning() && File.Exists(DesktopExe)){
   try{Process.Start(new ProcessStartInfo(DesktopExe){UseShellExecute=true});}catch{}
  }
  Ack(token,c,"queued","awaiting_recorder",now);
 }
  static void WriteHeartbeat(bool ok,string error){
  try{
   string path=Path.Combine(Folder,"firebase-heartbeat.json");
   string temp=path+".tmp";
   string safe=System.Text.RegularExpressions.Regex.IsMatch(error??"", "^[A-Za-z_][A-Za-z0-9_:-]{0,60}$")?error:"cloud_unavailable";
   File.WriteAllText(temp,J.Serialize(new{updatedAt=DateTime.UtcNow.ToString("o"),ok=ok,error=safe}),Encoding.UTF8);
   if(File.Exists(path))File.Replace(temp,path,null);
   else File.Move(temp,path);
  }catch{}
 } static void Loop(){
  while(Working) {
   try {
    string refresh,uid,id;
    lock(Sync){refresh=RefreshToken;uid=Uid;id=IdToken;}
    if(string.IsNullOrEmpty(refresh)||string.IsNullOrEmpty(uid)){WriteHeartbeat(false,"credentials_unavailable");Thread.Sleep(3000);continue;}
    string approved="";
    try{approved=File.ReadAllText(OwnerFile,Encoding.UTF8).Trim();}catch{}
    if(approved!=uid){WriteHeartbeat(false,"owner_unapproved");Thread.Sleep(7000);continue;}
    if(string.IsNullOrEmpty(id)||DateTime.UtcNow>=TokenExpiry){
     var res=Exchange(refresh);
     lock(Sync){
      if(Uid!=uid)continue;
      IdToken=GetValue(res,"id_token");
      string rotated=GetValue(res,"refresh_token");
      if(!string.IsNullOrEmpty(rotated)){RefreshToken=rotated;File.WriteAllBytes(CloudKey,Encrypt(rotated));}
      TokenExpiry=DateTime.UtcNow.AddMinutes(48);id=IdToken;
     }
    }
    long now=Now();
    var local=ReadLocalState();
    // Complete an earlier command (including after a bridge restart) before reading a new one.
    ProcessPending(id,local,now);
    HandleCloudCommand(id,uid,Request("GET","command",id),now);
    Request("PUT","status",id,J.Serialize(PublishableState(local,now)));WriteHeartbeat(true,"");
    Thread.Sleep(2400);
   }catch(WebException e){
    var r=e.Response as HttpWebResponse;
    WriteHeartbeat(false,r==null?"network_unavailable":"http_"+((int)r.StatusCode).ToString());if(r!=null && (r.StatusCode==HttpStatusCode.Unauthorized || r.StatusCode==HttpStatusCode.Forbidden)){
     lock(Sync){IdToken=null;TokenExpiry=DateTime.MinValue;}
    }
    Thread.Sleep(7000);
   }catch(Exception e){WriteHeartbeat(false,e.GetType().Name);Thread.Sleep(5000);}
  }
 }
}
