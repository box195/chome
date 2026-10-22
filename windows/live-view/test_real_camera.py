"""Bounded device-read test: reads a few still frames, saves no camera images."""
import time
from on_demand_camera import CameraPreview,camera_name,recorder_running
from av import open as open_video
from io import BytesIO
def main():
    if recorder_running():
        print("PC_DEVICE_CAMERA_TEST_SKIP_RECORDER_RUNNING")
        return
    if not camera_name():
        print("PC_DEVICE_CAMERA_TEST_SKIP_UNCONFIGURED")
        return
    camera=CameraPreview()
    try:
        camera.start()
        done=False
        deadline=time.monotonic()+12
        while time.monotonic()<deadline:
            payload,stamp=camera.get()
            if payload:
                with open_video(BytesIO(payload),format="mjpeg") as reader:
                    frame=next(iter(reader.decode(video=0)),None)
                if frame is not None:
                    print("PC_REAL_USB_CAMERA_PREVIEW_FRAME_PASS",frame.width,frame.height)
                    done=True
                    break
            time.sleep(.25)
        if not done:
            print("PC_REAL_USB_CAMERA_PREVIEW_FRAME_FAIL")
            raise SystemExit(2)
    finally:
        camera.close()
if __name__=="__main__":
    main()