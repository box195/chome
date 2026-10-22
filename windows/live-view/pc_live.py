"""Read-only-on-camera PC live preview. Reuses closed TS segments, never reopens USB camera."""
import asyncio
import glob
import io
import json
import os
import sys
import msvcrt
import pathlib
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import deque
from fractions import Fraction

import av
from aiortc import RTCConfiguration, RTCIceServer, RTCPeerConnection, RTCSessionDescription, VideoStreamTrack
from aiortc.sdp import candidate_from_sdp
from on_demand_camera import CameraPreview,recorder_running

ROOT = "https://chome-a8970-default-rtdb.firebaseio.com"
API_KEY = "AIzaSyBeA0nsSOJ9HbV7ZLt4XAcgYX6uWfP7ANo"
BASE = pathlib.Path(__file__).resolve().parents[1]
SECRETS_HELPER = BASE / "push-sender" / "ReadPushSecrets.exe"
VIDEO_FOLDER = pathlib.Path(os.environ.get("CHOME_VIDEO_ROOT",str(pathlib.Path.home() / "Videos" / "USBHomeCam")))


class OwnerAuth:
    def __init__(self):
        raw = subprocess.check_output([str(SECRETS_HELPER)], creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        credentials = json.loads(raw)
        self.uid = credentials["uid"]
        self.refresh = credentials["refreshToken"]
        self.token = ""
        self.expiry = 0

    def id_token(self):
        if self.token and time.time() < self.expiry:
            return self.token
        params = urllib.parse.urlencode({
            "grant_type": "refresh_token", "refresh_token": self.refresh
        }).encode("ascii")
        req = urllib.request.Request(
            "https://securetoken.googleapis.com/v1/token?key=" + API_KEY, params)
        with urllib.request.urlopen(req, timeout=12) as resp:
            data = json.load(resp)
        if data.get("user_id") != self.uid:
            raise RuntimeError("owner_uid_mismatch")
        self.token = data["id_token"]
        self.refresh = data.get("refresh_token", self.refresh)
        self.expiry = time.time() + min(2700, int(data.get("expires_in", 3600)) - 180)
        return self.token

    def request(self, path, method="GET", body=None):
        url = (ROOT + "/clients/" + urllib.parse.quote(self.uid) + "/pcLive/" +
               path + ".json?auth=" + urllib.parse.quote(self.id_token()))
        payload = json.dumps(body).encode("utf-8") if body is not None else None
        req = urllib.request.Request(url, data=payload, method=method)
        if payload is not None:
            req.add_header("Content-Type", "application/json")
        with urllib.request.urlopen(req, timeout=12) as resp:
            return json.load(resp)


class ClosedSegmentTrack(VideoStreamTrack):
    """Live-ish PC preview from the recorder's closed TS segments (about 1-3 s lag)."""
    def __init__(self):
        super().__init__()
        self.frames = deque()
        self.seen = set()
        self.n = 0
        self.last_preview_stamp = 0
        self.camera=CameraPreview()
        self.camera_stamp=0
        self.check_owner_at=0
        self.recorder_is_running=False

    def _fill(self):
        # Camera is owned by the existing recorder; do NOT open DirectShow a second time.
        candidates = []
        for directory in ("raw", "raw-motion"):
            candidates += glob.glob(str(VIDEO_FOLDER / directory / "*" / "*.ts"))
        candidates = sorted(candidates, key=lambda p: os.path.getmtime(p))
        # Newest segment may still be written. Only read closed files.
        for path in candidates[-7:-1]:
            if time.time() - os.path.getmtime(path) > 35:
                continue
            if path in self.seen:
                continue
            self.seen.add(path)
            try:
                if os.path.getsize(path) < 1024:
                    continue
                with av.open(path, mode="r") as container:
                    stream = container.streams.video[0]
                    frames = list(container.decode(stream))
                # Low overhead: preview uses up to roughly 8 FPS.
                self.frames.extend(frames[::max(1, len(frames)//8)][:10])
            except Exception:
                continue
        # The recorder or motion detector keeps a single atomically replaced JPG
        # frame for preview; there is no idle video recording.
        image_path = pathlib.Path(os.environ.get("CHOME_PREVIEW_JPEG",str(pathlib.Path(os.environ["LOCALAPPDATA"]) / "USBHomeCamNAVER" / "remote" / "preview.jpg")))
        try:
            stamp = image_path.stat().st_mtime_ns
            if stamp != self.last_preview_stamp and time.time_ns() - stamp < 4_000_000_000:
                with image_path.open("rb") as stream:
                    payload = stream.read(400000)
                with av.open(io.BytesIO(payload), format="mjpeg") as image:
                    snapshots = list(image.decode(video=0))
                if snapshots:
                    self.last_preview_stamp = stamp
                    # Prefer current camera frames over old recorded TS files.
                    self.frames.clear()
                    self.frames.extend(snapshots[-1:])
        except Exception:
            pass
        now=time.monotonic()
        if now-self.check_owner_at>=1.5:
            self.check_owner_at=now
            self.recorder_is_running=recorder_running()
            if self.recorder_is_running:self.camera.close()
        if not self.recorder_is_running:
            # Camera opens only for an authenticated WebRTC viewer.
            self.camera.start()
            picture,stamp=self.camera.get()
            if picture and stamp!=self.camera_stamp:
                try:
                    with av.open(io.BytesIO(picture),format="mjpeg") as image:
                        frames=list(image.decode(video=0))
                    if frames:
                        self.camera_stamp=stamp
                        self.frames.clear()
                        self.frames.extend(frames[-1:])
                except Exception:pass
        if len(self.seen) > 1000:
            self.seen = set(candidates[-40:])

    def stop(self):
        self.camera.close()
        super().stop()
    async def recv(self):
        while not self.frames:
            await asyncio.to_thread(self._fill)
            if not self.frames:
                await asyncio.sleep(0.15)
        frame = self.frames.popleft()
        self.n += 1
        frame.pts = int(self.n * 90000 / 8)
        frame.time_base = Fraction(1, 90000)
        await asyncio.sleep(0.125)
        return frame


async def poll_ice_candidates(owner, sid, peer):
    """Apply browser ICE candidates arriving after the offer was published."""
    processed = set()
    started = time.monotonic()
    while peer.connectionState not in ("connected", "failed", "closed") and time.monotonic()-started < 95:
        try:
            candidates = await asyncio.to_thread(owner.request, "offerCandidates/"+sid)
            if isinstance(candidates, dict):
                for key, data in candidates.items():
                    if key in processed or not isinstance(data, dict):
                        continue
                    raw = data.get("candidate")
                    if not isinstance(raw, str) or not raw.startswith("candidate:") or len(raw)>2000:
                        processed.add(key)
                        continue
                    try:
                        ice = candidate_from_sdp(raw[len("candidate:"):])
                        ice.sdpMid = data.get("sdpMid") or "0"
                        ice.sdpMLineIndex = int(data.get("sdpMLineIndex") or 0)
                        await peer.addIceCandidate(ice)
                    except Exception:
                        pass
                    processed.add(key)
        except Exception:
            pass
        await asyncio.sleep(0.65)


async def answer_offer(owner, offer):
    sid = offer.get("id")
    sdp = offer.get("sdp")
    created = offer.get("createdAt")
    if not isinstance(sid, str) or len(sid) != 32 or not all(c in "0123456789abcdef" for c in sid):
        return None
    if not isinstance(sdp, str) or len(sdp) > 32768 or not isinstance(created, (int,float)):
        return None
    if abs(time.time()*1000-created)>60000:
        return None
    peer = RTCPeerConnection(RTCConfiguration(
        iceServers=[RTCIceServer(urls=["stun:stun.l.google.com:19302"])]))
    pump=None
    try:
        track=ClosedSegmentTrack()
        peer.addTrack(track)
        peer._chome_track=track
        await peer.setRemoteDescription(RTCSessionDescription(sdp=sdp,type="offer"))
        pump=asyncio.create_task(poll_ice_candidates(owner,sid,peer))
        await peer.setLocalDescription(await peer.createAnswer())
        await asyncio.to_thread(owner.request,"answer","PUT",{
            "id":sid,"sdp":peer.localDescription.sdp,"createdAt":int(time.time()*1000)
        })
        print("CHOME_PC_LIVE_ANSWER_WRITTEN")
        return peer,pump
    except Exception:
        if pump:
            pump.cancel()
            await asyncio.gather(pump,return_exceptions=True)
        await close_camera_peer(peer)
        raise


async def close_camera_peer(peer):
    if peer is None:return
    track=getattr(peer,"_chome_track",None)
    if track:track.stop()
    await peer.close()

async def main():
    lock_path=pathlib.Path(os.environ["LOCALAPPDATA"])/"USBHomeCamNAVER"/"remote"/"pc-live.lock"
    lock_path.parent.mkdir(parents=True,exist_ok=True)
    lock_file=open(lock_path,"a+b")
    lock_file.seek(0)
    if lock_file.read(1)==b"":
        lock_file.write(b"0")
        lock_file.flush()
    try:
        lock_file.seek(0)
        msvcrt.locking(lock_file.fileno(),msvcrt.LK_NBLCK,1)
    except OSError:
        print("CHOME_PC_LIVE_ALREADY_RUNNING")
        return
    owner=OwnerAuth()
    if "--self-test" in sys.argv:
        await asyncio.to_thread(owner.request,"offer")
        print("CHOME_PC_LIVE_AUTH_TEST_PASS")
        return
    last=""
    peer=None
    pump=None
    print("CHOME_PC_WEBRTC_SIGNALING_ACTIVE")
    try:
        while True:
            try:
                offer=await asyncio.to_thread(owner.request,"offer")
                sid=(offer or {}).get("id") if isinstance(offer,dict) else None
                if sid and sid!=last:
                    last=sid
                    if pump:
                        pump.cancel()
                        await asyncio.gather(pump,return_exceptions=True)
                        pump=None
                    if peer:await close_camera_peer(peer)
                    result=await answer_offer(owner,offer)
                    if result:peer,pump=result
                elif not sid and peer:
                    if pump:
                        pump.cancel()
                        await asyncio.gather(pump,return_exceptions=True)
                        pump=None
                    await close_camera_peer(peer)
                    peer=None
                    last=""
                if peer and peer.connectionState in ("closed","failed"):
                    await close_camera_peer(peer)
                    peer=None
                    if pump:
                        pump.cancel()
                        await asyncio.gather(pump,return_exceptions=True)
                        pump=None
            except Exception as error:
                print("CHOME_PC_LIVE_SIGNAL_ERROR",type(error).__name__)
            await asyncio.sleep(1.0)
    finally:
        if pump:
            pump.cancel()
            await asyncio.gather(pump,return_exceptions=True)
        if peer:await close_camera_peer(peer)


if __name__=="__main__":
    asyncio.run(main())