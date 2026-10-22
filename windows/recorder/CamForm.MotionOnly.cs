using System;
using System.IO;
using System.Windows.Forms;

internal sealed partial class CamForm {
 bool motionOnly=false;
 DateTime lastMotionPrune=DateTime.MinValue;
 ComboBox pcRecordingModeBox;
 void LoadRecordingMode(){
  try{motionOnly=File.ReadAllText(Path.Combine(remotePath,"recording-mode.txt")).Trim()=="MOTION";}
  catch{motionOnly=false;}
 }
 void SetRecordingMode(bool selected){
  if(motionOnly==selected)return;
  bool active=recording;
  if(active)StopRecording();
  motionOnly=selected;
  try{
   Directory.CreateDirectory(remotePath);
   File.WriteAllText(Path.Combine(remotePath,"recording-mode.txt"),selected?"MOTION":"CONTINUOUS");
  }catch(Exception e){Log("Mode save error: "+Limit(e.Message,90));}
  if(pcRecordingModeBox!=null){
   int index=motionOnly?1:0;
   if(pcRecordingModeBox.SelectedIndex!=index)pcRecordingModeBox.SelectedIndex=index;
  }
  if(active)StartRecording();
 }
 void SetupRecordingModeUI(FlowLayoutPanel panel){
  panel.Controls.Add(new Label{Text="PC mode",Width=65,Height=26,TextAlign=System.Drawing.ContentAlignment.MiddleLeft});
  pcRecordingModeBox=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=180};
  pcRecordingModeBox.Items.AddRange(new object[]{"Continuous","Motion events only"});
  pcRecordingModeBox.SelectedIndex=motionOnly?1:0;
  pcRecordingModeBox.SelectedIndexChanged+=delegate{SetRecordingMode(pcRecordingModeBox.SelectedIndex==1);};
  panel.Controls.Add(pcRecordingModeBox);
 }
 void PruneMotionPrebuffer(){
  if((DateTime.Now-lastMotionPrune).TotalSeconds<45)return;
  lastMotionPrune=DateTime.Now;
  try{
   string root=Path.Combine(recordRoot,"raw-motion");
   if(!Directory.Exists(root))return;
   string active;lock(segmentSync){active=activeSegmentPath;}
   foreach(string path in Directory.GetFiles(root,"*.ts",SearchOption.AllDirectories)){
    if(string.Equals(path,active,StringComparison.OrdinalIgnoreCase))continue;
    var info=new FileInfo(path);
    if(DateTime.UtcNow-info.LastWriteTimeUtc<TimeSpan.FromMinutes(10))continue;
    try{File.Delete(path);}catch{}
   }
  }catch(Exception ex){Log("Motion prebuffer prune deferred: "+Limit(ex.Message,90));}
 }
}