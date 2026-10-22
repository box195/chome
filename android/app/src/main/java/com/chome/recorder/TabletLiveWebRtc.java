package com.chome.recorder;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import androidx.annotation.NonNull;
import androidx.camera.core.ImageProxy;
import com.google.firebase.database.*;
import org.webrtc.*;
import java.nio.ByteBuffer;
import java.util.*;

/** Firebase carries signaling only. The existing CameraX analyzer supplies video. */
final class TabletLiveWebRtc {
    private final Context context;
    private final DatabaseReference offer,answer;
    private final Handler main=new Handler(Looper.getMainLooper());
    private final Object gate=new Object();
    private PeerConnectionFactory factory;
    private PeerConnection peer;
    private VideoSource source;
    private VideoTrack track;
    private CapturerObserver observer;
    private ValueEventListener listener;
    private String active="";
    private long lastFrame;
    private boolean closed;
    TabletLiveWebRtc(Context c,String owner){
        context=c.getApplicationContext();
        DatabaseReference base=FirebaseDatabase.getInstance().getReference(
            "clients/"+owner+"/devices/y700/live");
        offer=base.child("offer");answer=base.child("answer");
    }
    void start(){
        try{
            PeerConnectionFactory.initialize(PeerConnectionFactory.InitializationOptions
                .builder(context).createInitializationOptions());
            EglBase egl=EglBase.create();
            factory=PeerConnectionFactory.builder()
                .setVideoEncoderFactory(new DefaultVideoEncoderFactory(egl.getEglBaseContext(),true,true))
                .setVideoDecoderFactory(new DefaultVideoDecoderFactory(egl.getEglBaseContext()))
                .createPeerConnectionFactory();
            source=factory.createVideoSource(false);
            observer=source.getCapturerObserver();observer.onCapturerStarted(true);
            track=factory.createVideoTrack("CHome-Y700",source);
        }catch(Exception e){return;}
        listener=new ValueEventListener(){
            @Override public void onDataChange(@NonNull DataSnapshot data){
                if(closed)return;
                String id=data.child("id").getValue(String.class);
                String sdp=data.child("sdp").getValue(String.class);
                Long time=data.child("createdAt").getValue(Long.class);
                if(id==null||sdp==null||time==null||!id.matches("[a-f0-9]{32}")||
                    sdp.length()>32768||Math.abs(System.currentTimeMillis()-time)>60000)return;
                synchronized(gate){if(active.equals(id))return;}
                open(id,sdp);
            }
            @Override public void onCancelled(@NonNull DatabaseError error){}
        };
        offer.addValueEventListener(listener);
    }
    void frame(ImageProxy image){
        synchronized(gate){
            if(closed||peer==null||observer==null)return;
            long now=android.os.SystemClock.elapsedRealtime();
            if(now-lastFrame<120)return;
            lastFrame=now;
            try{
                int w=image.getWidth(),h=image.getHeight();
                if(w<16||h<16||w>1920||h>1080)return;
                JavaI420Buffer buf=JavaI420Buffer.allocate(w,h);
                ImageProxy.PlaneProxy[] planes=image.getPlanes();
                ByteBuffer[] out={buf.getDataY(),buf.getDataU(),buf.getDataV()};
                int[] strides={buf.getStrideY(),buf.getStrideU(),buf.getStrideV()};
                for(int c=0;c<3;c++){
                    ByteBuffer in=planes[c].getBuffer();
                    int ww=c==0?w:(w+1)/2,hh=c==0?h:(h+1)/2;
                    int stride=planes[c].getRowStride(),pixel=planes[c].getPixelStride();
                    int start=in.position(),limit=in.limit();
                    for(int y=0;y<hh;y++)for(int x=0;x<ww;x++){
                        int k=start+y*stride+x*pixel;
                        if(k>=limit){buf.release();return;}
                        out[c].put(y*strides[c]+x,in.get(k));
                    }
                }
                VideoFrame v=new VideoFrame(buf,image.getImageInfo().getRotationDegrees(),System.nanoTime());
                observer.onFrameCaptured(v);v.release();
            }catch(Exception ignored){}
        }
    }
    void open(String id,String sdp){
        synchronized(gate){
            if(closed||factory==null)return;
            closePeer();
            active=id;
            List<PeerConnection.IceServer> ice=Collections.singletonList(
                PeerConnection.IceServer.builder("stun:stun.l.google.com:19302").createIceServer());
            PeerConnection.RTCConfiguration cfg=new PeerConnection.RTCConfiguration(ice);
            cfg.sdpSemantics=PeerConnection.SdpSemantics.UNIFIED_PLAN;
            peer=factory.createPeerConnection(cfg,new PeerConnection.Observer(){
                @Override public void onSignalingChange(PeerConnection.SignalingState state){}
                @Override public void onIceConnectionChange(PeerConnection.IceConnectionState state){
                    if(state==PeerConnection.IceConnectionState.FAILED)
                        main.post(()->{synchronized(gate){if(active.equals(id))closePeer();}});
                }
                @Override public void onIceConnectionReceivingChange(boolean b){}
                @Override public void onIceGatheringChange(PeerConnection.IceGatheringState s){
                    if(s==PeerConnection.IceGatheringState.COMPLETE)writeAnswer(id);
                }
                @Override public void onIceCandidate(IceCandidate candidate){}
                @Override public void onIceCandidatesRemoved(IceCandidate[] candidates){}
                @Override public void onAddStream(MediaStream stream){}
                @Override public void onRemoveStream(MediaStream stream){}
                @Override public void onDataChannel(DataChannel channel){}
                @Override public void onRenegotiationNeeded(){}
                @Override public void onAddTrack(RtpReceiver receiver,MediaStream[] streams){}
            });
            if(peer==null)return;
            peer.addTrack(track,Collections.singletonList("CHome"));
            peer.setRemoteDescription(new SdpObserver(){
                @Override public void onSetSuccess(){
                    synchronized(gate){
                        if(peer==null||!active.equals(id))return;
                        peer.createAnswer(new SdpObserver(){
                            @Override public void onCreateSuccess(SessionDescription local){
                                synchronized(gate){
                                    if(peer!=null&&active.equals(id))peer.setLocalDescription(new EmptySdp(),local);
                                }
                            }
                            @Override public void onSetSuccess(){}
                            @Override public void onCreateFailure(String s){}
                            @Override public void onSetFailure(String s){}
                        },new MediaConstraints());
                    }
                }
                @Override public void onCreateSuccess(SessionDescription s){}
                @Override public void onCreateFailure(String s){}
                @Override public void onSetFailure(String s){}
            },new SessionDescription(SessionDescription.Type.OFFER,sdp));
        }
    }
    private void writeAnswer(String id){
        synchronized(gate){
            if(closed||peer==null||!active.equals(id))return;
            SessionDescription d=peer.getLocalDescription();
            if(d==null||d.description==null)return;
            Map<String,Object> packet=new HashMap<>();
            packet.put("id",id);packet.put("sdp",d.description);
            packet.put("createdAt",System.currentTimeMillis());
            answer.setValue(packet);
        }
    }
    private void closePeer(){
        if(peer!=null){peer.dispose();peer=null;}
        active="";
    }
    void stop(){
        closed=true;
        if(listener!=null)offer.removeEventListener(listener);
        synchronized(gate){
            closePeer();
            if(observer!=null)observer.onCapturerStopped();
            if(track!=null)track.dispose();
            if(source!=null)source.dispose();
            if(factory!=null)factory.dispose();
        }
    }
    static class EmptySdp implements SdpObserver {
        @Override public void onCreateSuccess(SessionDescription s){}
        @Override public void onSetSuccess(){}
        @Override public void onCreateFailure(String s){}
        @Override public void onSetFailure(String s){}
    }
}