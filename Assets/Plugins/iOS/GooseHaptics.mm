// Native haptics bridge for Egg Snatcher / Goose Brawl.
// Called from Assets/Scripts/Core/HapticsService.cs via [DllImport("__Internal")].
//
// Two layers live in this file:
//
//   1. UIKit feedback generators (UIImpactFeedbackGenerator / UINotificationFeedbackGenerator).
//      The original bridge, kept unchanged as the fallback.
//      Exports: GooseHaptics_IsSupported / Prepare / Impact / Notification.
//
//   2. Core Haptics (CHHapticEngine, iOS 13+; the game targets iOS 16).
//      Transients with intensity + sharpness, continuous events with a parameter-curve fade and
//      the authored patterns used by gameplay.
//      Exports: GooseHaptics_CoreAvailable / Transient / Continuous / Pattern / SetPaused.
//      These do nothing (silently) when Core Haptics is unavailable; they never fall back to
//      UIKit themselves - the C# side decides that.
//
// Every engine call runs on the main queue. Nothing in here throws or crashes if the engine
// fails: errors are logged with an "[GooseHaptics]" prefix (throttled) and the request is dropped.
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <CoreHaptics/CoreHaptics.h>

// ---------------------------------------------------------------------------------------------
// UIKit fallback generators (unchanged)
// ---------------------------------------------------------------------------------------------

static UIImpactFeedbackGenerator *g_LightGen = nil;
static UIImpactFeedbackGenerator *g_MediumGen = nil;
static UIImpactFeedbackGenerator *g_HeavyGen = nil;
static UINotificationFeedbackGenerator *g_NotifyGen = nil;

static void GooseHaptics_EnsureGenerators(void)
{
    if (g_LightGen == nil) {
        g_LightGen = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleLight];
        g_MediumGen = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleMedium];
        g_HeavyGen = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleHeavy];
        g_NotifyGen = [[UINotificationFeedbackGenerator alloc] init];
    }
}

// ---------------------------------------------------------------------------------------------
// Core Haptics
// ---------------------------------------------------------------------------------------------

static CHHapticEngine *g_Engine = nil;
static BOOL g_EngineRunning = NO;        // best-effort mirror of the engine state
static BOOL g_EngineUnavailable = NO;    // hardware unsupported or creation failed: stop retrying
static BOOL g_LoggedStartFailure = NO;   // log a start failure once until a start succeeds
static int  g_PlayFailureLogs = 0;       // throttle per-request failure logging
static NSMutableDictionary<NSNumber *, CHHapticPattern *> *g_PatternCache = nil;

static const int kPatternCount = 10;     // ids 0..9, must match HapticsService.Pattern in C#
static const int kIntensitySteps = 20;   // authored patterns are cached per 1/20 intensity step
static const int kMaxPlayFailureLogs = 5;

static float ClampUnit(float v)
{
    if (v != v) return 0.0f; // NaN
    return v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v);
}

static float ClampRange(float v, float lo, float hi)
{
    if (v != v) return lo; // NaN
    return v < lo ? lo : (v > hi ? hi : v);
}

static void RunOnMain(dispatch_block_t block)
{
    dispatch_async(dispatch_get_main_queue(), block);
}

static void RunOnMainSync(dispatch_block_t block)
{
    if ([NSThread isMainThread]) block();
    else dispatch_sync(dispatch_get_main_queue(), block);
}

static NSString *StoppedReasonName(CHHapticEngineStoppedReason reason)
{
    switch (reason) {
        case CHHapticEngineStoppedReasonAudioSessionInterrupt: return @"audio session interrupt";
        case CHHapticEngineStoppedReasonApplicationSuspended:  return @"application suspended";
        case CHHapticEngineStoppedReasonIdleTimeout:           return @"idle timeout";
        case CHHapticEngineStoppedReasonNotifyWhenFinished:    return @"notify when finished";
        case CHHapticEngineStoppedReasonSystemError:           return @"system error";
        default:                                               return @"other";
    }
}

// Creates the shared engine once. Returns YES when an engine exists. Main queue only.
static BOOL CreateEngineIfNeeded(void)
{
    if (g_Engine != nil) return YES;
    if (g_EngineUnavailable) return NO;

    if (![[CHHapticEngine capabilitiesForHardware] supportsHaptics]) {
        g_EngineUnavailable = YES;
        NSLog(@"[GooseHaptics] Core Haptics not supported on this device; UIKit generators only.");
        return NO;
    }

    NSError *error = nil;
    CHHapticEngine *engine = [[CHHapticEngine alloc] initAndReturnError:&error];
    if (engine == nil) {
        g_EngineUnavailable = YES;
        NSLog(@"[GooseHaptics] CHHapticEngine creation failed: %@", error);
        return NO;
    }

    engine.playsHapticsOnly = YES;
    engine.autoShutdownEnabled = NO;

    __weak CHHapticEngine *weakEngine = engine;
    engine.resetHandler = ^{
        // The system reset the engine (media server restart etc.). Restart it right away; cached
        // CHHapticPattern objects are plain data and stay valid.
        NSLog(@"[GooseHaptics] Engine reset by the system; restarting.");
        g_EngineRunning = NO;
        CHHapticEngine *strongEngine = weakEngine;
        if (strongEngine == nil) return;
        NSError *startError = nil;
        if ([strongEngine startAndReturnError:&startError]) {
            g_EngineRunning = YES;
        } else {
            NSLog(@"[GooseHaptics] Engine restart after reset failed: %@", startError);
        }
    };
    engine.stoppedHandler = ^(CHHapticEngineStoppedReason reason) {
        // EnsureRunning() restarts lazily on the next request.
        g_EngineRunning = NO;
        NSLog(@"[GooseHaptics] Engine stopped (reason %ld: %@).", (long)reason, StoppedReasonName(reason));
    };

    g_Engine = engine;
    if (g_PatternCache == nil) g_PatternCache = [NSMutableDictionary dictionary];
    return YES;
}

// Starts the engine if it is not running. Returns YES when the engine is running. Main queue only.
static BOOL EnsureRunning(void)
{
    if (!CreateEngineIfNeeded()) return NO;
    if (g_EngineRunning) return YES;

    NSError *error = nil;
    if ([g_Engine startAndReturnError:&error]) {
        g_EngineRunning = YES;
        g_LoggedStartFailure = NO;
        return YES;
    }
    if (!g_LoggedStartFailure) {
        g_LoggedStartFailure = YES;
        NSLog(@"[GooseHaptics] Engine start failed: %@", error);
    }
    return NO;
}

// --- Pattern building helpers -----------------------------------------------------------------

static CHHapticEvent *TransientEvent(NSTimeInterval time, float intensity, float sharpness)
{
    CHHapticEventParameter *i = [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticIntensity
                                                                              value:ClampUnit(intensity)];
    CHHapticEventParameter *s = [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticSharpness
                                                                              value:ClampUnit(sharpness)];
    return [[CHHapticEvent alloc] initWithEventType:CHHapticEventTypeHapticTransient
                                         parameters:@[i, s]
                                       relativeTime:time];
}

static CHHapticEvent *ContinuousEvent(NSTimeInterval time, NSTimeInterval duration, float intensity, float sharpness)
{
    CHHapticEventParameter *i = [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticIntensity
                                                                              value:ClampUnit(intensity)];
    CHHapticEventParameter *s = [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticSharpness
                                                                              value:ClampUnit(sharpness)];
    return [[CHHapticEvent alloc] initWithEventType:CHHapticEventTypeHapticContinuous
                                         parameters:@[i, s]
                                       relativeTime:time
                                           duration:duration];
}

static CHHapticParameterCurveControlPoint *CurvePoint(NSTimeInterval time, float value)
{
    return [[CHHapticParameterCurveControlPoint alloc] initWithRelativeTime:time value:ClampUnit(value)];
}

// An intensity multiplier curve. Note that it multiplies EVERY event of the pattern that is active
// between its control points (transients included), it is not tied to a single event.
static CHHapticParameterCurve *IntensityCurve(NSArray<CHHapticParameterCurveControlPoint *> *points)
{
    return [[CHHapticParameterCurve alloc] initWithParameterID:CHHapticDynamicParameterIDHapticIntensityControl
                                                 controlPoints:points
                                                  relativeTime:0.0];
}

static CHHapticPattern *MakePattern(NSArray<CHHapticEvent *> *events,
                                    NSArray<CHHapticParameterCurve *> *curves,
                                    NSString *label)
{
    NSError *error = nil;
    CHHapticPattern *pattern = [[CHHapticPattern alloc] initWithEvents:events parameterCurves:curves error:&error];
    if (pattern == nil) {
        NSLog(@"[GooseHaptics] Could not build pattern %@: %@", label, error);
    }
    return pattern;
}

// Authored patterns. `scale` (0..1) multiplies every event intensity; the intensity curves are
// left alone because they are multipliers on top of the event intensities. Baking the scale into
// the events (rather than sending a HapticIntensityControl dynamic parameter) is deliberate: a
// parameter curve on HapticIntensityControl would override such a parameter, so Windup, Catch and
// RageRumble would ignore the requested intensity.
//
// The id order MUST match HapticsService.Pattern in C#:
//   0 Heartbeat, 1 WingBeat, 2 LungeWindup, 3 LungeLaunch, 4 Catch,
//   5 EggCrack, 6 RageRumble, 7 Success, 8 Landing, 9 Footstep
static CHHapticPattern *BuildAuthoredPattern(int id, float scale)
{
    switch (id) {
        case 0: // Heartbeat: lub ... dub
            return MakePattern(@[TransientEvent(0.00, 1.0f * scale, 0.25f),
                                 TransientEvent(0.17, 0.6f * scale, 0.20f)],
                               @[], @"Heartbeat");

        case 1: // WingBeat: two soft flaps
            return MakePattern(@[TransientEvent(0.00, 0.45f * scale, 0.15f),
                                 TransientEvent(0.09, 0.30f * scale, 0.15f)],
                               @[], @"WingBeat");

        case 2: // LungeWindup: 0.45 s rumble ramping 0.2 -> 0.8
            return MakePattern(@[ContinuousEvent(0.0, 0.45, 1.0f * scale, 0.3f)],
                               @[IntensityCurve(@[CurvePoint(0.00, 0.2f), CurvePoint(0.45, 0.8f)])],
                               @"LungeWindup");

        case 3: // LungeLaunch: hard hit with a short tail
            return MakePattern(@[TransientEvent(0.0, 1.0f * scale, 0.9f),
                                 ContinuousEvent(0.0, 0.18, 0.5f * scale, 0.6f)],
                               @[], @"LungeLaunch");

        case 4: // Catch: double hit + rumble that dies out. The curve holds at 1 through the
                // second hit so only the rumble fades (0.7 -> 0).
            return MakePattern(@[TransientEvent(0.00, 1.0f * scale, 0.7f),
                                 TransientEvent(0.06, 0.8f * scale, 0.5f),
                                 ContinuousEvent(0.0, 0.55, 0.7f * scale, 0.15f)],
                               @[IntensityCurve(@[CurvePoint(0.00, 1.0f), CurvePoint(0.06, 1.0f), CurvePoint(0.55, 0.0f)])],
                               @"Catch");

        case 5: // EggCrack: sharp crack + three tiny splinters
            return MakePattern(@[TransientEvent(0.00, 0.8f * scale, 0.95f),
                                 TransientEvent(0.06, 0.2f * scale, 0.90f),
                                 TransientEvent(0.11, 0.2f * scale, 0.90f),
                                 TransientEvent(0.18, 0.2f * scale, 0.90f)],
                               @[], @"EggCrack");

        case 6: // RageRumble: 0.8 s swell 0.35 -> 0.65 -> 0.2
            return MakePattern(@[ContinuousEvent(0.0, 0.8, 1.0f * scale, 0.2f)],
                               @[IntensityCurve(@[CurvePoint(0.0, 0.35f), CurvePoint(0.4, 0.65f), CurvePoint(0.8, 0.2f)])],
                               @"RageRumble");

        case 7: // Success: rising triple tap
            return MakePattern(@[TransientEvent(0.00, 0.5f * scale, 0.5f),
                                 TransientEvent(0.12, 0.7f * scale, 0.5f),
                                 TransientEvent(0.24, 1.0f * scale, 0.5f)],
                               @[], @"Success");

        case 8: // Landing: soft thud + very short settle
            return MakePattern(@[TransientEvent(0.0, 0.55f * scale, 0.3f),
                                 ContinuousEvent(0.0, 0.08, 0.3f * scale, 0.2f)],
                               @[], @"Landing");

        case 9: // Footstep: single soft tap at the requested intensity
            return MakePattern(@[TransientEvent(0.0, scale, 0.12f)],
                               @[], @"Footstep");

        default:
            return nil;
    }
}

// Returns the cached pattern for (id, quantised intensity), building it on first use.
static CHHapticPattern *CachedAuthoredPattern(int id, float scale)
{
    int step = (int)lroundf(ClampUnit(scale) * kIntensitySteps);
    if (step < 1) step = 1;
    NSNumber *key = @(id * 100 + step);

    if (g_PatternCache == nil) g_PatternCache = [NSMutableDictionary dictionary];
    CHHapticPattern *pattern = g_PatternCache[key];
    if (pattern == nil) {
        pattern = BuildAuthoredPattern(id, (float)step / (float)kIntensitySteps);
        if (pattern != nil) g_PatternCache[key] = pattern;
    }
    return pattern;
}

// Creates a one-shot player for `pattern` and starts it immediately. Main queue only.
static void PlayPattern(CHHapticPattern *pattern, NSString *label)
{
    if (pattern == nil) return;
    if (!EnsureRunning()) return;

    NSError *error = nil;
    id<CHHapticPatternPlayer> player = [g_Engine createPlayerWithPattern:pattern error:&error];
    if (player != nil && [player startAtTime:CHHapticTimeImmediate error:&error]) return;

    // Most likely the system stopped the engine behind our back (the stoppedHandler runs on a
    // background queue, so g_EngineRunning may be stale). Restart once and retry.
    g_EngineRunning = NO;
    if (EnsureRunning()) {
        NSError *retryError = nil;
        player = [g_Engine createPlayerWithPattern:pattern error:&retryError];
        if (player != nil && [player startAtTime:CHHapticTimeImmediate error:&retryError]) return;
        error = retryError;
    }

    if (g_PlayFailureLogs < kMaxPlayFailureLogs) {
        g_PlayFailureLogs++;
        NSLog(@"[GooseHaptics] Could not play %@: %@", label, error);
    }
}

// ---------------------------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------------------------

extern "C" {

// ---- UIKit layer (original API, behaviour unchanged) ----

int GooseHaptics_IsSupported(void)
{
    // UIImpactFeedbackGenerator exists on iOS 10+; the game targets iOS 16+.
    return 1;
}

void GooseHaptics_Prepare(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        GooseHaptics_EnsureGenerators();
        [g_LightGen prepare];
        [g_MediumGen prepare];
        [g_HeavyGen prepare];
        [g_NotifyGen prepare];
    });
}

// style: 0 = light, 1 = medium, 2 = heavy
void GooseHaptics_Impact(int style)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        GooseHaptics_EnsureGenerators();
        UIImpactFeedbackGenerator *gen = g_MediumGen;
        if (style == 0) gen = g_LightGen;
        else if (style >= 2) gen = g_HeavyGen;
        [gen impactOccurred];
        [gen prepare];
    });
}

// type: 0 = success, 1 = warning, 2 = error
void GooseHaptics_Notification(int type)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        GooseHaptics_EnsureGenerators();
        UINotificationFeedbackType t = UINotificationFeedbackTypeSuccess;
        if (type == 1) t = UINotificationFeedbackTypeWarning;
        else if (type >= 2) t = UINotificationFeedbackTypeError;
        [g_NotifyGen notificationOccurred:t];
        [g_NotifyGen prepare];
    });
}

// ---- Core Haptics layer ----

// 1 when the hardware supports haptics and the engine could be created. Also warm-starts the
// engine so the first haptic has no start-up latency.
int GooseHaptics_CoreAvailable(void)
{
    __block int available = 0;
    RunOnMainSync(^{
        available = CreateEngineIfNeeded() ? 1 : 0;
        if (available) EnsureRunning();
    });
    return available;
}

// One transient tap. intensity / sharpness are clamped to 0..1.
void GooseHaptics_Transient(float intensity, float sharpness)
{
    float i = ClampUnit(intensity);
    float s = ClampUnit(sharpness);
    RunOnMain(^{
        PlayPattern(MakePattern(@[TransientEvent(0.0, i, s)], @[], @"Transient"), @"Transient");
    });
}

// One continuous buzz of `duration` seconds (clamped 0.02..2.0). Full intensity for the first
// 70 % of the duration, then a linear fade to zero so it never ends abruptly.
void GooseHaptics_Continuous(float duration, float intensity, float sharpness)
{
    float d = ClampRange(duration, 0.02f, 2.0f);
    float i = ClampUnit(intensity);
    float s = ClampUnit(sharpness);
    RunOnMain(^{
        CHHapticParameterCurve *fade = IntensityCurve(@[CurvePoint(0.0, 1.0f), CurvePoint(d * 0.7, 1.0f), CurvePoint(d, 0.0f)]);
        PlayPattern(MakePattern(@[ContinuousEvent(0.0, d, i, s)], @[fade], @"Continuous"), @"Continuous");
    });
}

// Authored pattern `id` (see BuildAuthoredPattern) scaled by `intensity` (0..1).
void GooseHaptics_Pattern(int id, float intensity)
{
    if (id < 0 || id >= kPatternCount) return;
    float scale = ClampUnit(intensity);
    if (scale <= 0.0f) return;
    RunOnMain(^{
        PlayPattern(CachedAuthoredPattern(id, scale), @"authored pattern");
    });
}

// paused != 0: stop the engine (app going to background). paused == 0: start it again.
void GooseHaptics_SetPaused(int paused)
{
    RunOnMain(^{
        if (g_Engine == nil) return; // never create an engine just to pause it
        if (paused != 0) {
            g_EngineRunning = NO;
            [g_Engine stopWithCompletionHandler:^(NSError * _Nullable error) {
                if (error != nil) NSLog(@"[GooseHaptics] Engine stop failed: %@", error);
            }];
        } else {
            EnsureRunning();
        }
    });
}

} // extern "C"
