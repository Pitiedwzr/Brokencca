#import <Foundation/Foundation.h>
#import <QuartzCore/QuartzCore.h>

// All iOS video timing, including clock synchronization, uses Core Animation's
// host clock so Metal's GPU and presented timestamps share the same epoch.
static inline uint64_t BCVideoNowUs(void) { return (uint64_t)(CACurrentMediaTime()*1000000.0); }

@interface BCVideoTiming : NSObject
@property(nonatomic) uint64_t frame, generation, capturedUs, receivedUs, decodeSubmittedUs, decodedUs, readyUs;
@property(nonatomic) uint64_t renderStartedUs, committedUs, refreshUs, targetPresentUs;
@property(nonatomic) uint64_t callbackUs;
@property(nonatomic) BOOL renderOnMainThread;
@property(nonatomic) BOOL enabled, redraw;
- (BCVideoTiming *)renderCopy;
- (void)gpuStarted:(uint64_t)started ended:(uint64_t)ended completed:(uint64_t)completed failed:(BOOL)failed;
- (void)presented:(uint64_t)timeUs;
- (void)dropped:(NSString *)reason;
@end
