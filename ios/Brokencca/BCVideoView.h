#import <UIKit/UIKit.h>
#import <CoreVideo/CoreVideo.h>
#import "BCVideoGeometry.h"

@interface BCVideoView : UIView
@property(nonatomic, copy) void (^geometryChanged)(BCVideoGeometry *geometry);
@property(nonatomic, copy) void (^presented)(uint64_t frame, uint64_t generation, uint64_t timeUs);
@property(nonatomic, readonly) BOOL available;
- (void)activateGeneration:(uint64_t)generation;
- (void)enqueueBuffer:(CVPixelBufferRef)buffer frame:(uint64_t)frame generation:(uint64_t)generation geometry:(BCVideoGeometry *)geometry;
- (void)setPaused:(BOOL)paused;
- (void)clear;
- (NSDictionary *)statistics;
@end
