// On-device speech recognition for GOOSED.: did the player shout the goose's name?
// Called from Assets/Scripts/Core/SpeechRecognizer.cs. Recognises a short WAV file with SFSpeechRecognizer and
// requiresOnDeviceRecognition = YES, so nothing leaves the phone. One result slot, polled from C# each frame.
#import <Foundation/Foundation.h>
#import <Speech/Speech.h>

static SFSpeechRecognizer *g_Recognizer = nil;
static BOOL g_Probed = NO;
static NSOperationQueue *g_Queue = nil;
static NSLock *g_Lock = nil;
static SFSpeechRecognitionTask *g_Task = nil;
static int g_RequestId = 0;
static int g_Status = 0;          // 0 pending, 1 done, -1 failed
static NSString *g_Best = @"";

static SFSpeechRecognizer *GooseSpeech_Recognizer(void)
{
    if (g_Probed) return g_Recognizer;
    g_Probed = YES;
    g_Lock = [[NSLock alloc] init];
    g_Queue = [[NSOperationQueue alloc] init];
    g_Queue.maxConcurrentOperationCount = 1;
    NSArray<NSLocale *> *candidates = @[[NSLocale currentLocale],
                                        [NSLocale localeWithLocaleIdentifier:@"en-US"],
                                        [NSLocale localeWithLocaleIdentifier:@"en-CA"],
                                        [NSLocale localeWithLocaleIdentifier:@"en-GB"]];
    for (NSLocale *locale in candidates) {
        SFSpeechRecognizer *rec = [[SFSpeechRecognizer alloc] initWithLocale:locale];
        if (rec == nil) continue;
        BOOL onDevice = NO;
        if (@available(iOS 13.0, *)) onDevice = rec.supportsOnDeviceRecognition;
        if (rec.isAvailable && onDevice) {
            rec.queue = g_Queue;
            g_Recognizer = rec;
            NSLog(@"[GooseSpeech] on-device recogniser: %@", locale.localeIdentifier);
            break;
        }
    }
    if (g_Recognizer == nil) NSLog(@"[GooseSpeech] no on-device recogniser available");
    return g_Recognizer;
}

extern "C" {

int GooseSpeech_Available(void)
{
    return GooseSpeech_Recognizer() != nil ? 1 : 0;
}

// 0 notDetermined, 1 denied, 2 restricted, 3 authorized (SFSpeechRecognizerAuthorizationStatus order).
int GooseSpeech_AuthorizationStatus(void)
{
    return (int)[SFSpeechRecognizer authorizationStatus];
}

void GooseSpeech_RequestAuthorization(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        [SFSpeechRecognizer requestAuthorization:^(SFSpeechRecognizerAuthorizationStatus status) {
            NSLog(@"[GooseSpeech] authorization: %ld", (long)status);
        }];
    });
}

// Starts recognising the WAV at wavPath. contextCsv biases the recogniser (the goose's name, "sorry", ...).
// The best partial result is published when the deadline passes. Returns the request id.
int GooseSpeech_Recognize(const char *wavPath, const char *contextCsv, float deadlineSeconds)
{
    SFSpeechRecognizer *rec = GooseSpeech_Recognizer();
    if (rec == nil || wavPath == NULL) return -1;
    NSString *path = [NSString stringWithUTF8String:wavPath];
    NSString *context = contextCsv ? [NSString stringWithUTF8String:contextCsv] : @"";
    [g_Lock lock];
    int reqId = ++g_RequestId;
    g_Status = 0;
    g_Best = @"";
    [g_Lock unlock];
    float deadline = deadlineSeconds > 0.2f ? deadlineSeconds : 2.0f;

    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        [g_Task cancel];
        SFSpeechURLRecognitionRequest *req = [[SFSpeechURLRecognitionRequest alloc] initWithURL:[NSURL fileURLWithPath:path]];
        req.shouldReportPartialResults = YES;
        req.taskHint = SFSpeechRecognitionTaskHintSearch;
        if (@available(iOS 13.0, *)) req.requiresOnDeviceRecognition = YES;
        if (@available(iOS 16.0, *)) req.addsPunctuation = NO;
        NSMutableArray<NSString *> *ctx = [NSMutableArray array];
        for (NSString *w in [context componentsSeparatedByString:@","]) {
            NSString *t = [w stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
            if (t.length) [ctx addObject:t];
        }
        if (ctx.count) req.contextualStrings = ctx;
        g_Task = [rec recognitionTaskWithRequest:req resultHandler:^(SFSpeechRecognitionResult *result, NSError *error) {
            [g_Lock lock];
            if (reqId == g_RequestId) {
                if (result != nil) g_Best = result.bestTranscription.formattedString ?: @"";
                if (result != nil && result.isFinal) g_Status = 1;
                else if (error != nil && g_Status == 0) g_Status = g_Best.length > 0 ? 1 : -1;
            }
            [g_Lock unlock];
        }];
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(deadline * NSEC_PER_SEC)), dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
            [g_Lock lock];
            if (reqId == g_RequestId && g_Status == 0) g_Status = g_Best.length > 0 ? 1 : -1;
            [g_Lock unlock];
            if (reqId == g_RequestId) [g_Task cancel];
        });
    });
    return reqId;
}

// 0 pending, 1 done (utf8Out filled), -1 failed, -2 stale request id.
int GooseSpeech_Poll(int reqId, unsigned char *utf8Out, int cap)
{
    if (utf8Out != NULL && cap > 0) utf8Out[0] = 0;
    if (g_Lock == nil) return -1;
    [g_Lock lock];
    int status;
    if (reqId != g_RequestId) status = -2;
    else {
        status = g_Status;
        if (status == 1 && utf8Out != NULL && cap > 1) {
            const char *utf8 = [g_Best UTF8String];
            size_t n = strlen(utf8);
            if (n > (size_t)(cap - 1)) n = (size_t)(cap - 1);
            memcpy(utf8Out, utf8, n);
            utf8Out[n] = 0;
        }
    }
    [g_Lock unlock];
    return status;
}

}
