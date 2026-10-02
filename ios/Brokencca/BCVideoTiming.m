#import "BCVideoTiming.h"

@interface BCVideoTiming () {
    uint64_t _gpuStartedUs, _gpuEndedUs, _gpuCompletedUs, _presentedUs;
    BOOL _gpuDone, _presentationDone, _logged;
}
@property(nonatomic, strong) NSLock *guard;
- (void)emit:(NSString *)outcome;
@end

@implementation BCVideoTiming
- (instancetype)init { if ((self=[super init])) _guard=[NSLock new]; return self; }
- (BCVideoTiming *)renderCopy {
    BCVideoTiming *t=[BCVideoTiming new];
    t.frame=self.frame; t.generation=self.generation; t.capturedUs=self.capturedUs; t.receivedUs=self.receivedUs;
    t.decodeSubmittedUs=self.decodeSubmittedUs; t.decodedUs=self.decodedUs; t.readyUs=self.readyUs;
    t.enabled=self.enabled; t.redraw=self.redraw || self.renderStartedUs!=0;
    return t;
}
- (void)gpuStarted:(uint64_t)started ended:(uint64_t)ended completed:(uint64_t)completed failed:(BOOL)failed {
    [self.guard lock];
    _gpuStartedUs=started; _gpuEndedUs=ended; _gpuCompletedUs=completed; _gpuDone=YES;
    if (failed) { _presentationDone=YES; _presentedUs=0; }
    [self emit:failed ? @"gpu-error" : (_presentedUs ? @"presented" : @"not-presented")];
    [self.guard unlock];
}
- (void)presented:(uint64_t)timeUs {
    [self.guard lock]; _presentationDone=YES; _presentedUs=timeUs;
    [self emit:timeUs ? @"presented" : @"not-presented"]; [self.guard unlock];
}
- (void)dropped:(NSString *)reason {
    [self.guard lock]; _gpuDone=_presentationDone=YES; [self emit:reason]; [self.guard unlock];
}
// Called under the lock; callback order is unspecified. Emit exactly once after
// both events, never treat GPU completion alone as an onscreen presentation.
- (void)emit:(NSString *)outcome {
    if (!self.enabled || _logged || !_gpuDone || !_presentationDone) return;
    _logged=YES;
    NSDictionary *record=@{@"kind":@"video_frame_ios",@"generation":@(self.generation),@"frame_id":@(self.frame),
        @"outcome":outcome,@"redraw":@(self.redraw),@"capture_us":@(self.capturedUs),@"received_us":@(self.receivedUs),
        @"decode_submitted_us":@(self.decodeSubmittedUs),@"decoded_us":@(self.decodedUs),@"ready_us":@(self.readyUs),
        @"render_started_us":@(self.renderStartedUs),@"committed_us":@(self.committedUs),
        @"gpu_started_us":@(_gpuStartedUs),@"gpu_ended_us":@(_gpuEndedUs),@"gpu_completed_us":@(_gpuCompletedUs),
        @"presented_us":@(_presentedUs)};
    NSData *json=[NSJSONSerialization dataWithJSONObject:record options:0 error:nil];
    NSLog(@"BCCA_VIDEO_FRAME %@",[[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding]);
}
@end
