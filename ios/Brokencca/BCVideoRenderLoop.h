#import <Foundation/Foundation.h>
#import <QuartzCore/CADisplayLink.h>

// Owns the display link and its run loop. The callback must not access UIKit.
@interface BCVideoRenderLoop : NSObject
- (instancetype)initWithTick:(void (^)(CADisplayLink *link))tick;
- (void)setPaused:(BOOL)paused;
- (void)stop;
@end
