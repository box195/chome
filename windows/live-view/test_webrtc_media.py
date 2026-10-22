"""Local real WebRTC encode/decode test with synthetic camera JPEG; no device/network account."""
import asyncio
import os
import pathlib
import subprocess
import tempfile
from aiortc import RTCPeerConnection, RTCConfiguration
from aiortc import RTCSessionDescription

async def test():
    with tempfile.TemporaryDirectory(prefix="chome-webrtc-") as temporary:
        path=pathlib.Path(temporary)
        still=path/"live.jpg"
        ffmpeg=pathlib.Path(os.environ["LOCALAPPDATA"])/"USBHomeCamNAVER"/"bin"/"ffmpeg.exe"
        subprocess.run([str(ffmpeg),"-nostdin","-hide_banner","-loglevel","error",
            "-y","-f","lavfi","-i","testsrc=size=320x180:rate=8",
            "-frames:v","1",str(still)],check=True)
        os.environ["CHOME_PREVIEW_JPEG"]=str(still)
        os.environ["CHOME_VIDEO_ROOT"]=str(path/"empty")
        os.mkdir(path/"empty")
        from pc_live import ClosedSegmentTrack
        receive=RTCPeerConnection(RTCConfiguration(iceServers=[]))
        send=RTCPeerConnection(RTCConfiguration(iceServers=[]))
        ready=asyncio.get_running_loop().create_future()
        @receive.on("track")
        async def ontrack(track):
            try:
                frame=await asyncio.wait_for(track.recv(),timeout=15)
                if not ready.done():ready.set_result(frame)
            except Exception as e:
                if not ready.done():ready.set_exception(e)
        async def refresh():
            while not ready.done():
                os.utime(still,None)
                await asyncio.sleep(.35)
        refresh_task=asyncio.create_task(refresh())
        try:
            receive.addTransceiver("video",direction="recvonly")
            await receive.setLocalDescription(await receive.createOffer())
            await send.setRemoteDescription(RTCSessionDescription(
                sdp=receive.localDescription.sdp,type="offer"))
            send.addTrack(ClosedSegmentTrack())
            await send.setLocalDescription(await send.createAnswer())
            await receive.setRemoteDescription(RTCSessionDescription(
                sdp=send.localDescription.sdp,type="answer"))
            frame=await asyncio.wait_for(ready,timeout=20)
            assert frame.width==320 and frame.height==180
            print("PC_WEBRTC_REAL_VIDEO_FRAME_RECEIVED_PASS")
        finally:
            refresh_task.cancel()
            await asyncio.gather(refresh_task,return_exceptions=True)
            await receive.close()
            await send.close()

if __name__=="__main__":
    asyncio.run(test())