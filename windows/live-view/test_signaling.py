"""Authenticated local SDP signaling smoke test; does not open any camera."""
import asyncio
import time
import uuid
from aiortc import RTCPeerConnection, RTCSessionDescription
from pc_live import OwnerAuth

async def main():
    owner=OwnerAuth()
    rtc=RTCPeerConnection()
    sid=uuid.uuid4().hex
    rtc.addTransceiver("video",direction="recvonly")
    try:
        await rtc.setLocalDescription(await rtc.createOffer())
        await asyncio.to_thread(owner.request,"offer","PUT",{
            "id":sid,"sdp":rtc.localDescription.sdp,"createdAt":int(time.time()*1000)})
        answer=None
        for _ in range(18):
            await asyncio.sleep(1)
            value=await asyncio.to_thread(owner.request,"answer")
            if isinstance(value,dict) and value.get("id")==sid:
                answer=value
                break
        if answer is None:
            raise AssertionError("No answer from installed PC preview worker")
        await rtc.setRemoteDescription(RTCSessionDescription(sdp=answer["sdp"],type="answer"))
        for _ in range(8):
            if rtc.connectionState=="connected":
                break
            await asyncio.sleep(1)
        print("PC_WEBRTC_SIGNALING_ANSWER_PASS")
        print("PC_WEBRTC_ICE_CONNECTED="+str(rtc.connectionState=="connected"))
    finally:
        await rtc.close()
        await asyncio.to_thread(owner.request,"offer","DELETE")
        await asyncio.to_thread(owner.request,"answer","DELETE")

if __name__=="__main__":
    asyncio.run(main())