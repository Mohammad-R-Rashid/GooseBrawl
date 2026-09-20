// Camera format inspection for Egg Snatcher: tells C# which ARKit video formats use the ultra-wide camera.
#import <ARKit/ARKit.h>
#import <AVFoundation/AVFoundation.h>

extern "C" {

// handle: an ARVideoFormat pointer (AR Foundation's XRCameraConfiguration.nativeConfigurationHandle on ARKit).
int GooseCamera_FormatInfo(void* handle, int* isUltraWide, int* width, int* height, int* fps)
{
    if (handle == NULL) return 0;
    id obj = (__bridge id)handle;
    if (![obj isKindOfClass:[ARVideoFormat class]]) return 0;
    ARVideoFormat* f = (ARVideoFormat*)obj;
    *width = (int)f.imageResolution.width;
    *height = (int)f.imageResolution.height;
    *fps = (int)f.framesPerSecond;
    *isUltraWide = 0;
    if (@available(iOS 14.5, *)) {
        if ([f.captureDeviceType isEqualToString:AVCaptureDeviceTypeBuiltInUltraWideCamera]) *isUltraWide = 1;
    }
    return 1;
}

// Audio session: Unity/FMOD picks the Ambient category, which obeys the silent switch, so the goose is mute the moment the
// ringer is off. Playback ignores the switch. Only applied while the microphone is NOT recording (recording needs PlayAndRecord).
int GooseAudio_ApplyPlayback(void)
{
    AVAudioSession* session = [AVAudioSession sharedInstance];
    NSError* err = nil;
    if ([session.category isEqualToString:AVAudioSessionCategoryPlayback]) return 2;
    BOOL ok = [session setCategory:AVAudioSessionCategoryPlayback withOptions:0 error:&err];
    if (ok) ok = [session setActive:YES error:&err];
    return ok ? 1 : 0;
}

// 0 nominal, 1 fair, 2 serious, 3 critical (NSProcessInfoThermalState). Sampled by PerfProbe for the spike logs.
int GooseCamera_ThermalState(void)
{
    return (int)[[NSProcessInfo processInfo] thermalState];
}

int GooseCamera_SupportedFormatCount(int* ultraWideCount)
{
    int uw = 0;
    NSArray<ARVideoFormat*>* formats = ARWorldTrackingConfiguration.supportedVideoFormats;
    if (@available(iOS 14.5, *)) {
        for (ARVideoFormat* f in formats) {
            if ([f.captureDeviceType isEqualToString:AVCaptureDeviceTypeBuiltInUltraWideCamera]) uw++;
        }
    }
    *ultraWideCount = uw;
    return (int)formats.count;
}

}
