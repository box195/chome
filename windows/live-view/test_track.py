"""No-camera test for the PC WebRTC video source."""
import asyncio
import pathlib
import subprocess
import tempfile
import pc_live

async def test():
    with tempfile.TemporaryDirectory(prefix="chome-track-") as temporary:
        base=pathlib.Path(temporary)
        folder=base/"raw"/"2026-10-22"
        folder.mkdir(parents=True)
        ffmpeg=pathlib.Path.home()/"AppData"/"Local"/"USBHomeCamNAVER"/"bin"/"ffmpeg.exe"
        if not ffmpeg.exists():
            raise RuntimeError("Test FFmpeg unavailable")
        for name in ("a","b"):
            subprocess.run([str(ffmpeg),"-nostdin","-hide_banner","-loglevel","error",
                "-f","lavfi","-i","testsrc=size=320x180:rate=8",
                "-t","2","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p",
                "-f","mpegts",str(folder/(name+".ts"))],check=True)
        pc_live.VIDEO_FOLDER=base
        track=pc_live.ClosedSegmentTrack()
        frame=await asyncio.wait_for(track.recv(),timeout=8)
        assert frame.width==320 and frame.height==180
        assert frame.pts>0
        print("PC_WEBRTC_CLOSED_SEGMENT_FRAME_TEST_PASS")
        image=base/"preview.jpg"
        subprocess.run([str(ffmpeg),"-nostdin","-hide_banner","-loglevel","error","-y",
            "-f","lavfi","-i","testsrc=size=320x180:rate=6",
            "-frames:v","1",str(image)],check=True)
        import os
        os.environ["CHOME_PREVIEW_JPEG"]=str(image)
        live=pc_live.ClosedSegmentTrack()
        live._fill()
        assert live.last_preview_stamp>0
        assert len(live.frames)>=1
        print("PC_WEBRTC_LIVE_JPEG_FRAME_TEST_PASS")

asyncio.run(test())