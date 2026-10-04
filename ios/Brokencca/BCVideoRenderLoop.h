#import <Foundation/Foundation.h>
#import <QuartzCore/CADisplayLink.h>

// Owns the display link and its run loop. Both callbacks must not access UIKit.
@interface BCVideoRenderLoop : NSObject
- (instancetype)initWithTick:(void (^)(CADisplayLink *link))tick render:(void (^)(void))render;
- (void)requestRender;
- (void)setPaused:(BOOL)paused;
- (void)stop;
@end
