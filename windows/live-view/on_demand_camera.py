"""Transient read-only PC camera preview, used ONLY while owner views live.

No video is recorded and the existing recorder is never opened a second time.
"""
import os
import pathlib
import subprocess
import threading
import time


ROOT = pathlib.Path(os.environ["LOCALAPPDATA"]) / "USBHomeCamNAVER"


def recorder_running():
    try:
        raw = subprocess.check_output(
            ["tasklist", "/NH", "/FI", "IMAGENAME eq USBHomeCam-NAVER-Controlled.exe"],
            stderr=subprocess.DEVNULL, creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0),
            timeout=4)
        return b"USBHomeCam-NAVER-Controlled.exe" in raw
    except Exception:
        return True  # Unknown device ownership: fail closed, do not open camera.


def camera_name():
    try:
        lines=(ROOT/"settings.txt").read_text(encoding="utf-8-sig",errors="replace").splitlines()
        for line in lines:
            if line.startswith("camera="):
                return line.split("=",1)[1].strip()
    except Exception:
        return ""
    return ""


class CameraPreview:
    def __init__(self):
        self.process=None
        self.worker=None
        self.guard=threading.Lock()
        self.latest_frame=None
        self.latest_stamp=0.0
        self.failed_until=0.0

    def start(self):
        if self.process is not None or time.monotonic()<self.failed_until:
            return
        name=camera_name()
        ffmpeg=ROOT/"bin"/"ffmpeg.exe"
        if not name or not ffmpeg.is_file() or recorder_running():
            return
        args=[str(ffmpeg),"-nostdin","-hide_banner","-loglevel","error",
            "-f","dshow","-i","video="+name,
            "-an","-vf","fps=6,scale=320:180:flags=fast_bilinear",
            "-c:v","mjpeg","-q:v","7","-f","image2pipe","-"]
        try:
            p=subprocess.Popen(args,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
        except OSError:
            self.failed_until=time.monotonic()+10
            return
        self.process=p
        self.worker=threading.Thread(target=self._read,args=(p,),daemon=True)
        self.worker.start()

    def _read(self,process):
        stream=process.stdout
        buf=bytearray()
        try:
            while process.poll() is None:
                chunk=stream.read(4096)
                if not chunk:
                    break
                buf.extend(chunk)
                while True:
                    a=buf.find(b"\xff\xd8")
                    if a<0:
                        if len(buf)>2:del buf[:-2]
                        break
                    if a>0:del buf[:a]
                    b=buf.find(b"\xff\xd9",2)
                    if b<0:break
                    jpg=bytes(buf[:b+2]);del buf[:b+2]
                    if len(jpg)<400000:
                        with self.guard:
                            self.latest_frame=jpg
                            self.latest_stamp=time.monotonic()
                if len(buf)>1200000:
                    buf.clear()
        finally:
            try:stream.close()
            except Exception:pass
            if self.process is process:self.process=None
            self.failed_until=time.monotonic()+5

    def get(self):
        with self.guard:
            if self.latest_frame and time.monotonic()-self.latest_stamp<2:
                return self.latest_frame,self.latest_stamp
        return None,None

    def close(self):
        p=self.process
        self.process=None
        if p is not None:
            try:
                p.terminate()
                p.wait(timeout=1.2)
            except Exception:
                try:p.kill()
                except Exception:pass
        with self.guard:
            self.latest_frame=None
            self.latest_stamp=0