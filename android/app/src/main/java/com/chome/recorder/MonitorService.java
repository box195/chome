package com.chome.recorder;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.os.*;
import android.net.wifi.WifiManager;
import android.widget.Toast;
import androidx.annotation.NonNull;
import androidx.camera.core.*;
import androidx.camera.lifecycle.ProcessCameraProvider;
import androidx.camera.video.*;
import androidx.core.app.NotificationCompat;
import androidx.core.content.ContextCompat;
import androidx.lifecycle.LifecycleService;
import com.google.common.util.concurrent.ListenableFuture;
import com.google.firebase.auth.*;
import com.google.firebase.database.*;
import java.io.File;
import java.nio.ByteBuffer;
import java.text.SimpleDateFormat;
import java.util.*;
import java.util.concurrent.*;

public final class MonitorService extends LifecycleService {
    private static final int NOTIFICATION=4207;
    private static final String CHANNEL="chome_y700_camera";
    private static final long SEGMENT_MS=30000L, POST_MS=120000L;
    private final Handler main=new Handler(Looper.getMainLooper());
    private final ExecutorService analyzer=Executors.newSingleThreadExecutor();
    private final MotionDetector detector=new MotionDetector();
    private PowerManager.WakeLock cpuLock;
    private WifiManager.WifiLock wifiLock;
    private TabletPushSender pushSender;
    private ProcessCameraProvider provider;
    private VideoCapture<Recorder> video;
    private Recording recording;
    private File currentFile;
    private DatabaseReference deviceRef, commandRef;
    private ValueEventListener commandListener;
    private String ownerUid="",myUid="",mode="OFF",lastMotion="";
    private boolean cameraReady=false,closing=false,finalizingClip=false;
    private long lastAnalyzed=0,lastMotionElapsed=0,lastCommandSeq=0,segmentStarted=0;
    private int motionDetections=0;
    private final Runnable ticker=new Runnable() {
        @Override public void run(){
            if(closing)return;
            try{
                long now=SystemClock.elapsedRealtime();
                if(cameraReady && mode.equals("CONTINUOUS") && recording==null)beginClip();
                if(recording!=null) {
                    if(mode.equals("CONTINUOUS") && now-segmentStarted>=SEGMENT_MS)endClip();
                    if(mode.equals("MOTION") && now-lastMotionElapsed>POST_MS)endClip();
                    if(mode.equals("OFF"))endClip();
                }
                publishState();
            }catch(Exception e){setLocalState("감시 오류: "+e.getClass().getSimpleName());}
            main.postDelayed(this,2600);
        }
    };
    @Override public void onCreate(){
        super.onCreate();
        createChannel();
        holdScreenOffResources();
        Notification n=new NotificationCompat.Builder(this,CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentTitle("CHome Y700 카메라 대기")
            .setContentText("폰 CHome에서 녹화를 수동으로 시작할 수 있습니다")
            .setContentIntent(PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),
                PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT))
            .setOngoing(true).build();
        if(Build.VERSION.SDK_INT>=29)startForeground(NOTIFICATION,n,ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA);
        else startForeground(NOTIFICATION,n);
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId){
        super.onStartCommand(intent,flags,startId);
        if(commandRef!=null)return START_NOT_STICKY;
        ownerUid=Settings.publicPrefs(this).getString(Settings.KEY_OWNER,"");
        FirebaseUser account=FirebaseAuth.getInstance().getCurrentUser();
        if(account==null||ownerUid.isEmpty()){setLocalState("Firebase 기기 인증/소유자 UID 필요");stopSelf();return START_NOT_STICKY;}
        myUid=account.getUid();
        lastCommandSeq=Settings.publicPrefs(this).getLong("lastY700CommandSeq",0L);
        setLocalState("카메라 연결 중");
        prepareCamera();
        // Wait for a fresh authenticated ID token before opening protected RTDB listeners.
        setLocalState("Firebase 인증 토큰 확인 중");
        account.getIdToken(true).addOnCompleteListener(result -> {
            if(closing)return;
            if(!result.isSuccessful() || result.getResult()==null || result.getResult().getToken()==null) {
                setLocalState("Firebase 토큰 발급 실패: "+(result.getException()==null?
                    "unknown":result.getException().getClass().getSimpleName()));
                return;
            }
            setLocalState("Firebase 인증 완료 / Y700 연결 확인 중");
            verifyPairAndAttach();
        });
        MyboxUploadWorker.schedule(this);
        main.post(ticker);
        return START_NOT_STICKY; // background camera re-start is not allowed by recent Android versions
    }
    private void holdScreenOffResources(){
        try{
            PowerManager pm=(PowerManager)getSystemService(POWER_SERVICE);
            if(pm!=null){
                cpuLock=pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"CHome:recordingCpu");
                cpuLock.setReferenceCounted(false);
                cpuLock.acquire();
            }
        }catch(Exception e){setLocalState("CPU wakelock unavailable");}
        try{
            WifiManager wm=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
            if(wm!=null){
                wifiLock=wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF,"CHome:recordingWifi");
                wifiLock.setReferenceCounted(false);
                wifiLock.acquire();
            }
        }catch(Exception e){setLocalState("WiFi wakelock unavailable");}
    }
    private void createChannel() {
        NotificationManager manager=getSystemService(NotificationManager.class);
        if(manager!=null)manager.createNotificationChannel(new NotificationChannel(CHANNEL,
            "CHome 카메라 감시",NotificationManager.IMPORTANCE_LOW));
    }
    private void prepareCamera(){
        ListenableFuture<ProcessCameraProvider> future=ProcessCameraProvider.getInstance(this);
        future.addListener(()->{
            if(closing)return;
            try{
                provider=future.get();
                Recorder recorder=new Recorder.Builder()
                    .setQualitySelector(QualitySelector.from(Quality.HD,FallbackStrategy.lowerQualityOrHigherThan(Quality.SD)))
                    .build();
                video=VideoCapture.withOutput(recorder);
                ImageAnalysis analysis=new ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build();
                analysis.setAnalyzer(analyzer,this::analyze);
                provider.unbindAll();
                provider.bindToLifecycle(this,CameraSelector.DEFAULT_BACK_CAMERA,analysis,video);
                cameraReady=true;setLocalState("카메라 준비 완료 / 폰 명령 대기");
                publishState();
            }catch(Exception e){cameraReady=false;setLocalState("카메라 연결 실패: "+e.getClass().getSimpleName());publishState();}
        },ContextCompat.getMainExecutor(this));
    }
    private void analyze(@NonNull ImageProxy frame){
        try{
            long now=SystemClock.elapsedRealtime();
            if(now-lastAnalyzed<450L)return;
            lastAnalyzed=now;
            final int w=frame.getWidth(),h=frame.getHeight();
            if(w<96||h<54)return;
            ImageProxy.PlaneProxy plane=frame.getPlanes()[0];
            ByteBuffer buf=plane.getBuffer();
            int rowStride=plane.getRowStride(),pixelStride=plane.getPixelStride();
            int offset=buf.position();
            byte[] pixels=new byte[MotionDetector.SIZE];
            for(int y=0;y<MotionDetector.H;y++){
                int yy=Math.min(h-1,y*h/MotionDetector.H);
                for(int x=0;x<MotionDetector.W;x++){
                    int xx=Math.min(w-1,x*w/MotionDetector.W);
                    pixels[y*MotionDetector.W+x]=buf.get(offset+yy*rowStride+xx*pixelStride);
                }
            }
            boolean found=detector.feed(pixels);
            if(found)main.post(()->onMotion());
        }catch(Exception e){/* A damaged frame must never stop camera analysis. */}
        finally{frame.close();}
    }
    private void onMotion(){
        if(closing||!mode.equals("MOTION"))return;
        long now=SystemClock.elapsedRealtime();
        if(now-lastMotionElapsed>=1000L){
            motionDetections++;
            lastMotion=new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ssXXX",Locale.US).format(new Date());
        }
        lastMotionElapsed=now;
        if(recording==null && cameraReady)beginClip();
        if(pushSender!=null)pushSender.motion(System.currentTimeMillis());
    }
    private void beginClip(){
        if(closing||recording!=null||finalizingClip||!cameraReady||mode.equals("OFF"))return;
        try{
            File dir=MyboxUploadWorker.videoFolder(this);
            if(!dir.exists()&&!dir.mkdirs())throw new IllegalStateException("영상 저장 폴더 생성 실패");
            String name=new SimpleDateFormat("yyyyMMdd_HHmmss_SSS",Locale.US).format(new Date())+".mp4";
            currentFile=new File(dir,name);
            final File finishedFile=currentFile;
            PendingRecording pending=video.getOutput().prepareRecording(this,
                new FileOutputOptions.Builder(currentFile).build());
            segmentStarted=SystemClock.elapsedRealtime();
            recording=pending.start(ContextCompat.getMainExecutor(this),event->{
                if(event instanceof VideoRecordEvent.Finalize){
                    VideoRecordEvent.Finalize finalized=(VideoRecordEvent.Finalize)event;
                    recording=null;
                    if(!finalized.hasError() && currentFile!=null && currentFile.length()>1024){
                        MyboxUploadWorker.schedule(this);
                    }
                    if(!closing && mode.equals("CONTINUOUS"))main.postDelayed(()->beginClip(),300);
                }
            });
            setLocalState("녹화 중 ("+(mode.equals("CONTINUOUS")?"상시":"움직임")+")");
        }catch(Exception e){recording=null;setLocalState("녹화 시작 오류: "+e.getClass().getSimpleName());}
    }
    private void endClip() {
        Recording previous=recording;recording=null;
        if(previous!=null){try{previous.stop();}catch(Exception ignored){}}
    }
    private void verifyPairAndAttach() {
        String path="clients/"+ownerUid+"/devices/y700/authUid";
        FirebaseDatabase.getInstance().getReference(path)
            .addListenerForSingleValueEvent(new ValueEventListener() {
                @Override public void onDataChange(@NonNull DataSnapshot snapshot) {
                    if(closing)return;
                    String approved=snapshot.getValue(String.class);
                    if(!myUid.equals(approved)){
                        setLocalState("Y700 기기 UID 미등록 또는 불일치: CHome에서 Y700 연결 다시 확인");
                        return;
                    }
                    setLocalState("Firebase 기기 승인 확인 완료 / 명령 연결 중");
                    attachFirebase();
                    if(pushSender==null)pushSender=new TabletPushSender(MonitorService.this,ownerUid);
                    publishState();
                }
                @Override public void onCancelled(@NonNull DatabaseError error) {
                    setLocalState("Firebase 기기 승인 조회 실패: "+error.getCode()+" "+error.getMessage());
                }
            });
    }
    private void attachFirebase(){
        deviceRef=FirebaseDatabase.getInstance().getReference("clients/"+ownerUid+"/devices/y700");
        commandRef=deviceRef.child("command");
        commandListener=new ValueEventListener(){
            @Override public void onDataChange(@NonNull DataSnapshot snapshot) {
                if(!snapshot.exists()||closing)return;
                String action=snapshot.child("action").getValue(String.class);
                String id=snapshot.child("id").getValue(String.class);
                String requester=snapshot.child("requester").getValue(String.class);
                String requestedMode=snapshot.child("mode").getValue(String.class);
                Long seq=snapshot.child("seq").getValue(Long.class),issuedAt=snapshot.child("issuedAt").getValue(Long.class);
                if(action==null||id==null||seq==null||issuedAt==null||!ownerUid.equals(requester)||seq<=lastCommandSeq)return;
                if(!id.matches("[a-f0-9]{32}")||Math.abs(System.currentTimeMillis()-issuedAt)>90000L)return;
                lastCommandSeq=seq;
                Settings.publicPrefs(MonitorService.this).edit().putLong("lastY700CommandSeq",seq).apply();
                if(!cameraReady) {ack(id,seq,action,"failed","camera_not_ready");return;}
                if("STOP".equals(action)){
                    mode="OFF";endClip();setLocalState("감시 준비 / 녹화 중지됨");
                    ack(id,seq,action,"applied","recording_stopped");publishState();
                }else if("START".equals(action)&&("MOTION".equals(requestedMode)||"CONTINUOUS".equals(requestedMode))){
                    String previous=mode;mode=requestedMode;
                    if(!previous.equals(mode))endClip();
                    if(mode.equals("CONTINUOUS"))beginClip();
                    setLocalState(mode.equals("MOTION")?"움직임 감지 대기":"상시 녹화 시작");
                    ack(id,seq,action,"applied","mode_"+mode);publishState();
                }else ack(id,seq,action,"failed","unknown_action_or_mode");
            }
            @Override public void onCancelled(@NonNull DatabaseError error){
                setLocalState("Firebase 명령 읽기 실패: "+error.getCode()+" "+error.getMessage());
            }
        };
        commandRef.addValueEventListener(commandListener);
    }
    private void ack(String id,long seq,String action,String phase,String detail){
        if(deviceRef==null)return;
        Map<String,Object> data=new HashMap<>();
        data.put("id",id);data.put("seq",seq);data.put("action",action);
        data.put("phase",phase);data.put("detail",detail);data.put("updatedAt",System.currentTimeMillis());
        deviceRef.child("ack").setValue(data);
    }
    private void publishState(){
        if(deviceRef==null||closing)return;
        File[] videos=MyboxUploadWorker.videoFolder(this).listFiles((d,n)->n.endsWith(".mp4"));
        int local=0,uploaded=0;
        if(videos!=null){
            local=videos.length;
            for(File f:videos)if(new File(f.getAbsolutePath()+".uploaded").exists())uploaded++;
        }
        Map<String,Object> data=new HashMap<>();
        data.put("bridgeAt",System.currentTimeMillis());
        data.put("monitoring",true);data.put("cameraConnected",cameraReady);
        data.put("recording",recording!=null);data.put("mode",mode);
        data.put("motionDetections",motionDetections);data.put("lastMotion",lastMotion);
        data.put("localClips",local);data.put("uploadedClips",uploaded);
        data.put("pendingUploads",local-uploaded);data.put("cloudConnected",!Settings.token(this).isEmpty());
        deviceRef.child("status").setValue(data,(error,ref)->{
            if(error!=null && !closing)setLocalState("Firebase 상태 쓰기 실패: "+error.getCode()+" "+error.getMessage());
        });
    }
    private void setLocalState(String text){
        Settings.publicPrefs(this).edit().putString("monitorState",text).apply();
    }
    @Override public void onDestroy(){
        closing=true;main.removeCallbacks(ticker);endClip();
        if(commandRef!=null&&commandListener!=null)commandRef.removeEventListener(commandListener);
        if(deviceRef!=null){Map<String,Object> offline=new HashMap<>();offline.put("monitoring",false);offline.put("recording",false);
            offline.put("cameraConnected",false);offline.put("mode","OFF");offline.put("bridgeAt",System.currentTimeMillis());
            deviceRef.child("status").updateChildren(offline);}
        try{if(provider!=null)provider.unbindAll();}catch(Exception e){}
        if(pushSender!=null)pushSender.close();
        if(wifiLock!=null && wifiLock.isHeld())wifiLock.release();
        if(cpuLock!=null && cpuLock.isHeld())cpuLock.release();
        analyzer.shutdownNow();setLocalState("감시 서비스 종료");
        super.onDestroy();
    }
}