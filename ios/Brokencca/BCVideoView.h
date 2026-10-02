#import <UIKit/UIKit.h>
#import <CoreVideo/CoreVideo.h>
#import "BCVideoGeometry.h"
#import "BCVideoTiming.h"

@interface BCVideoView : UIView
@property(nonatomic, copy) void (^geometryChanged)(BCVideoGeometry *geometry);
@property(nonatomic, copy) void (^presented)(uint64_t frame, uint64_t generation, uint64_t timeUs, uint64_t revision);
@property(nonatomic, readonly) BOOL available;
@property(nonatomic) BOOL zoomToPlayfield;
- (void)activateGeneration:(uint64_t)generation;
- (void)enqueueBuffer:(CVPixelBufferRef)buffer frame:(uint64_t)frame generation:(uint64_t)generation geometry:(BCVideoGeometry *)geometry timing:(BCVideoTiming *)timing;
- (void)setPaused:(BOOL)paused;
- (void)clear;
- (NSDictionary *)statistics;
- (BOOL)isGenerationActive:(uint64_t)generation revision:(uint64_t)revision;
@end
