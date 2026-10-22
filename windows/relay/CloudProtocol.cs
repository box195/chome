using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

internal sealed class CloudCommand {
 public long Seq;
 public string Id;
 public string Action;
 public string Mode;
 public long IssuedAt;
}

internal static class CloudProtocol {
 internal const long MaxAgeMs=90000, FutureSkewMs=15000, ResultTimeoutMs=45000;
 static readonly Regex IdFormat=new Regex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
 internal static string Field(Dictionary<string,object> o,string key) {
  object v;
  return o!=null && o.TryGetValue(key,out v) && v!=null
   ? Convert.ToString(v,CultureInfo.InvariantCulture):"";
 }
 internal static bool Parse(Dictionary<string,object> data,string uid,long lastSeq,long now,out CloudCommand cmd,out string reason) {
  cmd=null;reason="invalid";
  if(data==null)return false;
  long seq,issued;
  if(!long.TryParse(Field(data,"seq"),NumberStyles.Integer,CultureInfo.InvariantCulture,out seq) || seq<=0) return false;
  if(seq<=lastSeq){reason="duplicate";return false;}
  if(seq<now-MaxAgeMs || seq>now+FutureSkewMs){reason="invalid_sequence_time";return false;}
  string id=Field(data,"id"),action=Field(data,"action"),requester=Field(data,"requester");
  if(!IdFormat.IsMatch(id) || (action!="START" && action!="STOP") || requester!=uid)return false;
  if(!long.TryParse(Field(data,"issuedAt"),NumberStyles.Integer,CultureInfo.InvariantCulture,out issued))return false;
  if(issued>now+FutureSkewMs || issued<now-MaxAgeMs){reason="stale";return false;}
  string mode=Field(data,"mode");
  if(mode!="" && mode!="MOTION" && mode!="CONTINUOUS")return false;
  cmd=new CloudCommand{Seq=seq,Id=id,Action=action,Mode=mode,IssuedAt=issued};
  reason="ok";return true;
 }
 internal static bool MatchesReceipt(Dictionary<string,object> data,CloudCommand pending) {
  if(data==null || pending==null)return false;
  long seq;
  return long.TryParse(Field(data,"seq"),out seq) && seq==pending.Seq &&
   Field(data,"id")==pending.Id && Field(data,"action")==pending.Action &&
   Field(data,"phase")=="handled";
 }
 internal static bool RecentState(Dictionary<string,object> state,long now) {
  DateTimeOffset stamp;
  return state!=null && DateTimeOffset.TryParse(Field(state,"updatedAt"),out stamp) &&
   Math.Abs(now-stamp.ToUnixTimeMilliseconds())<=12000;
 }
 internal static bool ResultApplied(CloudCommand pending,Dictionary<string,object> receipt,Dictionary<string,object> state,long now){
  if(!MatchesReceipt(receipt,pending) || !RecentState(state,now))return false;
  return Field(state,"recording").ToLowerInvariant()==(pending.Action=="START"?"true":"false") &&
    (pending.Action!="START" || string.IsNullOrEmpty(pending.Mode) || Field(state,"mode")==pending.Mode);
 }
 internal static int RunTests(){
  int n=0;Action<bool,string> check=(b,msg)=>{if(!b)throw new Exception("FAIL "+msg);n++;};
  long now=1792500000000L;
  var x=new Dictionary<string,object>{{"id",new string('a',32)},{"seq",now},{"action","START"},{"requester","owner01"},{"issuedAt",now}};
  CloudCommand cmd;string why;
  check(Parse(x,"owner01",now-1,now,out cmd,out why),"fresh");
  check(!Parse(x,"owner01",now,now,out cmd,out why)&&why=="duplicate","duplicate");
  check(!Parse(x,"other",9,now,out cmd,out why),"wrong owner");
  x["issuedAt"]=now-MaxAgeMs-1;check(!Parse(x,"owner01",now-1,now,out cmd,out why)&&why=="stale","expired");
  x["issuedAt"]=now+FutureSkewMs+1;check(!Parse(x,"owner01",now-1,now,out cmd,out why),"future");
  x["issuedAt"]=now;x["action"]="ERASE";check(!Parse(x,"owner01",now-1,now,out cmd,out why),"unsupported action");
  x["action"]="START";x["id"]="../commands";check(!Parse(x,"owner01",now-1,now,out cmd,out why),"path injection");
  x["id"]=new string('a',32);check(Parse(x,"owner01",now-1,now,out cmd,out why),"valid again");
  var receipt=new Dictionary<string,object>{{"id",cmd.Id},{"seq",cmd.Seq},{"action",cmd.Action},{"phase","handled"}};
  var state=new Dictionary<string,object>{{"updatedAt",DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o")},{"recording",true}};
  check(ResultApplied(cmd,receipt,state,now),"actual confirmation");
  state["recording"]=false;check(!ResultApplied(cmd,receipt,state,now),"not recording");
  state["recording"]=true;check(!ResultApplied(cmd,receipt,state,now+20000),"stale recorder");
  receipt["seq"]=now+1;check(!ResultApplied(cmd,receipt,state,now),"mismatched receipt");
  x["mode"]="INVALID";
  check(!Parse(x,"owner01",now-1,now,out cmd,out why),"reject invalid mode");
  x["mode"]="MOTION";
  check(Parse(x,"owner01",now-1,now,out cmd,out why) && cmd.Mode=="MOTION","accept motion mode");
  receipt["seq"]=now;
  state["mode"]="CONTINUOUS";
  check(!ResultApplied(cmd,receipt,state,now),"reject incorrect recorder mode");
  state["mode"]="MOTION";
  check(ResultApplied(cmd,receipt,state,now),"confirm requested motion mode");  Console.WriteLine("CLOUD_PROTOCOL_TESTS_PASS="+n);
  return 0;
 }
}
