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
    private final DatabaseReference offer,answer,diagnostics,offerCandidates,answerCandidates;
    private volatile String liveState="starting";
    private final Handler main=new Handler(Looper.getMainLooper());
    private final Object gate=new Object();
    private PeerConnectionFactory factory;
    private PeerConnection peer;
    private VideoSource source;
    private VideoTrack track;
    private EglBase egl;
    private CapturerObserver observer;
    private ValueEventListener listener;
    private ChildEventListener remoteIceListener;
    private String remoteIceSession="";
    private String active="";
    private long lastFrame;
    private boolean closed;
    TabletLiveWebRtc(Context c,String owner){
        context=c.getApplicationContext();
        DatabaseReference base=FirebaseDatabase.getInstance().getReference(
            "clients/"+owner+"/devices/y700/live");
        offer=base.child("offer");answer=base.child("answer");diagnostics=base.child("state");
        offerCandidates=base.child("offerCandidates");answerCandidates=base.child("answerCandidates");
    }
    void start(){
        try{
            PeerConnectionFactory.initialize(PeerConnectionFactory.InitializationOptions
                .builder(context).createInitializationOptions());
            egl=EglBase.create();
            factory=PeerConnectionFactory.builder()
                .setVideoEncoderFactory(new DefaultVideoEncoderFactory(egl.getEglBaseContext(),true,true))
                .setVideoDecoderFactory(new DefaultVideoDecoderFactory(egl.getEglBaseContext()))
                .createPeerConnectionFactory();
            source=factory.createVideoSource(false);
            observer=source.getCapturerObserver();observer.onCapturerStarted(true);
            track=factory.createVideoTrack("CHome-Y700",source);
        }catch(Exception e){updateState("android_webrtc_init_failed");return;}
        listener=new ValueEventListener(){
            @Override public void onDataChange(@NonNull DataSnapshot data){
                if(closed)return;
                String id=data.child("id").getValue(String.class);
                String sdp=data.child("sdp").getValue(String.class);
                Long time=data.child("createdAt").getValue(Long.class);
                if(id==null||sdp==null||time==null||!id.matches("[a-f0-9]{32}")||
                    sdp.length()>32768||Math.abs(System.currentTimeMillis()-time)>60000)return;
                synchronized(gate){if(active.equals(id))return;}
                updateState("offer_received");open(id,sdp);
            }
            @Override public void onCancelled(@NonNull DatabaseError error){updateState("firebase_offer_read_denied");}
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
            updateState("offer_processing");
            List<PeerConnection.IceServer> ice=Collections.singletonList(
                PeerConnection.IceServer.builder("stun:stun.l.google.com:19302").createIceServer());
            PeerConnection.RTCConfiguration cfg=new PeerConnection.RTCConfiguration(ice);
            cfg.sdpSemantics=PeerConnection.SdpSemantics.UNIFIED_PLAN;
            peer=factory.createPeerConnection(cfg,new PeerConnection.Observer(){
                @Override public void onSignalingChange(PeerConnection.SignalingState state){}
                @Override public void onIceConnectionChange(PeerConnection.IceConnectionState state){
                    updateState("ice_"+state.name().toLowerCase(java.util.Locale.ROOT));
                    if(state==PeerConnection.IceConnectionState.FAILED)
                        main.post(()->{synchronized(gate){if(active.equals(id))closePeer();}});
                }
                @Override public void onIceConnectionReceivingChange(boolean b){}
                @Override public void onIceGatheringChange(PeerConnection.IceGatheringState s){
                    if(s==PeerConnection.IceGatheringState.COMPLETE)writeAnswer(id);
                }
                @Override public void onIceCandidate(IceCandidate candidate){
                    if(candidate==null||closed)return;
                    synchronized(gate){
                        if(!id.equals(active)||peer==null)return;
                        String text=candidate.sdp;
                        if(text==null||text.length()>2000)return;
                        Map<String,Object> packet=new HashMap<>();
                        packet.put("candidate",text);
                        packet.put("sdpMid",candidate.sdpMid==null?"0":candidate.sdpMid);
                        packet.put("sdpMLineIndex",candidate.sdpMLineIndex);
                        packet.put("createdAt",System.currentTimeMillis());
                        String key=java.util.UUID.randomUUID().toString().replace("-","");
                        answerCandidates.child(id).child(key).setValue(packet,
                            (error,reference)->{if(error!=null)updateState("answer_ice_write_failed");});
                    }
                }
                @Override public void onIceCandidatesRemoved(IceCandidate[] candidates){}
                @Override public void onAddStream(MediaStream stream){}
                @Override public void onRemoveStream(MediaStream stream){}
                @Override public void onDataChannel(DataChannel channel){}
                @Override public void onRenegotiationNeeded(){}
                @Override public void onAddTrack(RtpReceiver receiver,MediaStream[] streams){}
            });
            if(peer==null){updateState("peer_creation_failed");return;}
            peer.setRemoteDescription(new SdpObserver(){
                @Override public void onSetSuccess(){
                    synchronized(gate){
                        if(peer==null||!active.equals(id))return;
                        peer.addTrack(track,Collections.singletonList("CHome"));
                        watchRemoteIce(id);
                        updateState("remote_offer_accepted");
                        peer.createAnswer(new SdpObserver(){
                            @Override public void onCreateSuccess(SessionDescription local){
                                synchronized(gate){
                                    if(peer!=null&&active.equals(id))peer.setLocalDescription(new SdpObserver(){
                                        @Override public void onCreateSuccess(SessionDescription desc){}
                                        @Override public void onSetSuccess(){
                                            updateState("local_answer_ready");
                                            synchronized(gate){
                                                if(peer!=null&&id.equals(active))writeAnswer(id);
                                            }
                                        }
                                        @Override public void onCreateFailure(String msg){updateState("answer_creation_failed");}
                                        @Override public void onSetFailure(String msg){updateState("answer_setting_failed");}
                                    },local);
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
    private void watchRemoteIce(String id){
        if(remoteIceListener!=null && !remoteIceSession.isEmpty())
            offerCandidates.child(remoteIceSession).removeEventListener(remoteIceListener);
        remoteIceSession=id;
        remoteIceListener=new ChildEventListener(){
            @Override public void onChildAdded(@NonNull DataSnapshot snapshot,String previous){
                acceptRemoteIce(id,snapshot);
            }
            @Override public void onChildChanged(@NonNull DataSnapshot snapshot,String previous){}
            @Override public void onChildRemoved(@NonNull DataSnapshot snapshot){}
            @Override public void onChildMoved(@NonNull DataSnapshot snapshot,String previous){}
            @Override public void onCancelled(@NonNull DatabaseError error){updateState("offer_ice_read_denied");}
        };
        offerCandidates.child(id).addChildEventListener(remoteIceListener);
    }
    private void acceptRemoteIce(String id,DataSnapshot snap){
        String candidate=snap.child("candidate").getValue(String.class);
        String mid=snap.child("sdpMid").getValue(String.class);
        Long index=snap.child("sdpMLineIndex").getValue(Long.class);
        if(candidate==null||candidate.length()>2000||index==null)return;
        synchronized(gate){
            if(!id.equals(active)||peer==null)return;
            if(!peer.addIceCandidate(new IceCandidate(mid==null?"0":mid,index.intValue(),candidate)))
                updateState("offer_ice_rejected");
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
            answer.setValue(packet,(error,r)->updateState(error==null?"answer_written":"answer_write_denied"));
        }
    }
    String getState(){return liveState;}
    private void updateState(String state){
        if(state==null||closed||state.equals(liveState))return;
        liveState=state;
        diagnostics.setValue(state);
    }    private void closePeer(){
        if(remoteIceListener!=null&&!remoteIceSession.isEmpty()){
            offerCandidates.child(remoteIceSession).removeEventListener(remoteIceListener);
            remoteIceListener=null;
            remoteIceSession="";
        }
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
            if(egl!=null)egl.release();
        }
    }
    static class EmptySdp implements SdpObserver {
        @Override public void onCreateSuccess(SessionDescription s){}
        @Override public void onSetSuccess(){}
        @Override public void onCreateFailure(String s){}
        @Override public void onSetFailure(String s){}
    }
}