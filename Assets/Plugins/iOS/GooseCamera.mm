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
