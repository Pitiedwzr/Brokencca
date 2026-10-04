#import "BCVideoRenderLoop.h"

@interface BCVideoRenderLoop ()
@property(atomic, strong) NSThread *thread;
@property(atomic) BOOL stopping, paused;
@property(nonatomic, copy) void (^tick)(CADisplayLink *link);
@property(nonatomic, copy) void (^render)(void);
@property(nonatomic) BOOL workerReady, renderPending; // protected by @synchronized(self)
@property(nonatomic, strong) CADisplayLink *displayLink; // render-thread ownership
- (void)run;
- (void)displayTick:(CADisplayLink *)link;
- (void)applyPause;
- (void)wake;
- (void)renderRequested;
@end

@implementation BCVideoRenderLoop
@synthesize paused=_paused;
- (instancetype)initWithTick:(void (^)(CADisplayLink *link))tick render:(void (^)(void))render {
    if ((self=[super init])) {
        _tick=[tick copy];
        _render=[render copy];
        _thread=[[NSThread alloc] initWithTarget:self selector:@selector(run) object:nil];
        _thread.name=@"Brokencca.VideoRender";
        _thread.qualityOfService=NSQualityOfServiceUserInteractive;
        [_thread start];
    }
    return self;
}
- (void)run {
    @autoreleasepool {
        // Keep an input source while the display link is paused, so runMode
        // blocks instead of spinning, and queued pause/stop selectors can wake it.
        NSPort *keepAlive=[NSMachPort port];
        [NSRunLoop.currentRunLoop addPort:keepAlive forMode:NSDefaultRunLoopMode];
        if (!self.stopping) {
            self.displayLink=[CADisplayLink displayLinkWithTarget:self selector:@selector(displayTick:)];
            self.displayLink.preferredFrameRateRange=CAFrameRateRangeMake(60,60,60);
            self.displayLink.paused=self.paused;
            [self.displayLink addToRunLoop:NSRunLoop.currentRunLoop forMode:NSRunLoopCommonModes];
            NSLog(@"BCCA_VIDEO render_loop=dedicated render_schedule=frame-arrival presentation=asap display_link_hz=60");
        }
        // Requests arriving before the thread starts are retained, not lost.
        @synchronized (self) { _workerReady=YES; }
        [self renderRequested];
        while (!self.stopping) {
            @autoreleasepool {
                [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode beforeDate:NSDate.distantFuture];
            }
        }
        @synchronized (self) { _workerReady=NO; _renderPending=NO; }
        [self.displayLink invalidate]; self.displayLink=nil;
        [NSRunLoop.currentRunLoop removePort:keepAlive forMode:NSDefaultRunLoopMode];
        [keepAlive invalidate];
        // NSThread retains its target until run returns. Break our ownership of
        // the thread as well, so a stopped loop doesn't retain itself forever.
        self.thread=nil;
    }
}
- (void)displayTick:(CADisplayLink *)link {
    @autoreleasepool { if (!self.stopping && !self.paused) self.tick(link); }
}
- (void)requestRender {
    @synchronized (self) {
        if (self.stopping || _renderPending) return;
        _renderPending=YES;
        if (_workerReady)
            [self performSelector:@selector(renderRequested) onThread:self.thread withObject:nil waitUntilDone:NO];
    }
}
- (void)renderRequested {
    @autoreleasepool {
        @synchronized (self) {
            if (!_renderPending || self.stopping) return;
            _renderPending=NO;
        }
        // The view rechecks pause, generation and available slots under its lock.
        // Clear its queued request even when pause overtakes a queued wake.
        self.render();
    }
}
- (void)setPaused:(BOOL)paused {
    @synchronized (self) { _paused=paused; }
    NSThread *thread=self.thread;
    if (thread.isExecuting && !self.stopping)
        [self performSelector:@selector(applyPause) onThread:thread withObject:nil waitUntilDone:NO];
}
- (BOOL)paused { @synchronized (self) { return _paused; } }
- (void)applyPause { self.displayLink.paused=self.paused; }
- (void)wake { } // scheduled selector wakes a paused run loop, without waiting on the UI thread
- (void)stop {
    self.stopping=YES;
    NSThread *thread=self.thread;
    if (thread.isExecuting && thread!=NSThread.currentThread)
        [self performSelector:@selector(wake) onThread:thread withObject:nil waitUntilDone:NO];
}
@end
