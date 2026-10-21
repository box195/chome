# CHome Y700 Android Recorder — Integration Draft

This source adds a separate Android recorder alongside the existing Windows CHome recorder.
There is no automatic weekday/weekend switching.

## Implemented in source
- Local YUV camera image analysis (96x54 gray, approximately 2 FPS) and detection algorithm from Windows.
- Modes MOTION, CONTINUOUS and OFF. Web owner manually changes mode per device.
- Motion mode starts MP4 after motion; stops approximately 120 seconds after the last motion.
- Continuous MP4 clips roll approximately every 30 seconds.
- Videos saved locally under Android app-specific Movies/CHome.
- MYBOX uploads through WorkManager and HTTPS. PAT is encrypted locally.
- Firebase uses /clients/{ownerUid}/devices/y700 for device-specific status, commands and acknowledgements. Original PC paths are unchanged.
- Android anonymous device identity must be explicitly approved by the Google-authenticated owner in CHome.
- Camera foreground service is started visibly from the tablet app; remote start cannot always revive a stopped camera process.

## External setup required
1. Register Android app com.chome.recorder in Firebase project chome-a8970. Replace provisional app ID in CHomeApp.java with registered Android app ID.
2. Enable Anonymous provider in Firebase Authentication for device sign-in.
3. Carefully review currently deployed RTDB security rules. Generate owner-specific rules using Prepare-Owner-Rules.ps1; test security rules before deployment. database.rules.json is a deny-all sentinel and must not be deployed instead.
4. Compile Android APK using GitHub Action android-y700.yml or a compatible Android SDK / Gradle environment.
5. Install APK on physical Y700. Grant CAMERA and notifications. Verify background screen-off behavior.
6. Configure owner UID, MYBOX PAT, optional folder ID in Android app. Pair Android device UID in CHome web panel.
7. Open tablet app while visible and start foreground monitor. Use web control for MOTION or CONTINUOUS and STOP.

## Limitations / unfinished validation
- Not yet validated on physical Y700.
- No 30-second Android pre-motion recording. Event video begins after motion and continues until 120s after last motion.
- Web Push background sender currently runs on PC, so PC-off closed-tab tablet motion push notifications are NOT complete.
- No upload deletion/PC archive lifecycle for tablet yet; local files remain after upload.
- MYBOX Android upload is implemented but not verified against a real account.
- Android screen-off and power/battery bypass stability require on-device tests.
- Debug APK is development-signed only.

## Security
- Owner access is Google-authenticated and explicitly bound to owner UID. Anonymous tablet may only use approved device paths after pairing.
- MYBOX PAT is not sent to Firebase or written to Git and uses encrypted Android preferences.
- Original clips are retained after upload; no unverified deletion is attempted.