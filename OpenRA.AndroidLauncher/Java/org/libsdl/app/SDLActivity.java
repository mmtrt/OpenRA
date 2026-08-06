package org.libsdl.app;

import android.app.Activity;
import android.os.Bundle;
import android.view.Surface;
import android.view.View;

/**
 * Minimal stub so libSDL2.so JNI_OnLoad does not abort with ClassNotFoundException.
 * OpenRA Android prototype uses SurfaceView + EGL, not SDLActivity as the real entry.
 */
public class SDLActivity extends Activity {
    protected static SDLActivity mSingleton;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        mSingleton = this;
    }

    public static Surface getNativeSurface() {
        return null;
    }

    public static View getContentView() {
        return mSingleton != null ? mSingleton.findViewById(android.R.id.content) : null;
    }

    public static void initJNI() {
        // no-op stub
    }
}
