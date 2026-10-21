package com.chome.recorder;

final class MotionDetector {
    static final int W=96,H=54,SIZE=W*H;
    private byte[] previous;
    double lastRatio;
    boolean feed(byte[] frame) {
        if(frame==null||frame.length!=SIZE)throw new IllegalArgumentException("bad frame");
        if(previous==null){previous=frame.clone();return false;}
        long sum=0;
        for(int i=0;i<SIZE;i++)sum+=(int)(frame[i]&255)-(int)(previous[i]&255);
        double shift=(double)sum/SIZE;
        int changed=0;
        for(int i=0;i<SIZE;i++){
            double diff=Math.abs((int)(frame[i]&255)-(int)(previous[i]&255)-shift);
            if(diff>=29)changed++;
        }
        lastRatio=(double)changed/SIZE;
        previous=frame.clone();
        return lastRatio>=0.014 && lastRatio<=0.65;
    }
}