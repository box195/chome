import fs from "node:fs";
const filename="database.rules.template.json";
const db=JSON.parse(fs.readFileSync(filename,"utf8"));
const owner="auth != null && auth.uid === '__OWNER_UID__' && $uid === '__OWNER_UID__' && auth.token.firebase.sign_in_provider === 'google.com'";
const tablet="auth != null && $uid === '__OWNER_UID__' && auth.token.firebase.sign_in_provider === 'anonymous' && auth.uid === root.child('clients/'+$uid+'/devices/y700/authUid').val()";
db.rules.clients["$uid"].devices={
  y700:{
    authUid:{".write":owner,".validate":"newData.isString() && newData.val().matches(/^[A-Za-z0-9_-]{15,128}$/)"},
    command:{".read":tablet,".write":owner,
      ".validate":"newData.hasChildren(['id','seq','action','mode','requester','issuedAt']) && newData.child('id').isString() && newData.child('id').val().matches(/^[a-f0-9]{32}$/) && newData.child('seq').isNumber() && newData.child('seq').val()>0 && (!data.exists() || newData.child('seq').val()>data.child('seq').val()) && newData.child('requester').val()===auth.uid && (newData.child('action').val()==='START' || newData.child('action').val()==='STOP') && (newData.child('mode').val()==='MOTION' || newData.child('mode').val()==='CONTINUOUS') && newData.child('issuedAt').isNumber() && newData.child('issuedAt').val()>=now-90000 && newData.child('issuedAt').val()<=now+15000 && newData.child('seq').val()>=now-90000 && newData.child('seq').val()<=now+15000"},
    status:{".write":tablet,
      ".validate":"newData.hasChildren(['bridgeAt','monitoring','cameraConnected','recording','mode','motionDetections','pendingUploads','localClips','uploadedClips','cloudConnected']) && newData.child('bridgeAt').isNumber() && newData.child('bridgeAt').val()>=now-90000 && newData.child('bridgeAt').val()<=now+15000 && newData.child('monitoring').isBoolean() && newData.child('cameraConnected').isBoolean() && newData.child('recording').isBoolean() && (newData.child('mode').val()==='OFF' || newData.child('mode').val()==='MOTION' || newData.child('mode').val()==='CONTINUOUS') && newData.child('motionDetections').isNumber() && newData.child('motionDetections').val()>=0 && newData.child('pendingUploads').isNumber() && newData.child('pendingUploads').val()>=0 && newData.child('localClips').isNumber() && newData.child('localClips').val()>=0 && newData.child('uploadedClips').isNumber() && newData.child('uploadedClips').val()>=0 && newData.child('cloudConnected').isBoolean()"},
    ack:{".write":tablet,
      ".validate":"newData.hasChildren(['id','seq','action','phase','detail','updatedAt']) && newData.child('id').isString() && newData.child('id').val().matches(/^[a-f0-9]{32}$/) && newData.child('seq').isNumber() && newData.child('seq').val()>0 && (newData.child('action').val()==='START' || newData.child('action').val()==='STOP') && (newData.child('phase').val()==='queued' || newData.child('phase').val()==='applied' || newData.child('phase').val()==='failed') && newData.child('detail').isString() && newData.child('updatedAt').isNumber()"}
  }
};
fs.writeFileSync(filename,JSON.stringify(db,null,2)+"\n","utf8");
console.log("Y700_RULE_TEMPLATE_PASS: PC rules preserved");