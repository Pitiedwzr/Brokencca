#import <Foundation/Foundation.h>
#import <CoreVideo/CoreVideo.h>
#import "BCVideoGeometry.h"

@interface BCVideoDecoder : NSObject
@property(nonatomic, copy) void (^decoded)(CVPixelBufferRef buffer, uint64_t frame, uint64_t generation, BCVideoGeometry *geometry);
@property(nonatomic, copy) void (^failed)(NSString *reason, uint64_t generation);
- (void)configure:(NSDictionary *)configuration generation:(uint64_t)generation completion:(void (^)(BOOL success, BOOL hardwareVerified, NSString *reason))completion;
- (void)decode:(NSData *)accessUnit frame:(uint64_t)frame capturedUs:(uint64_t)captured generation:(uint64_t)generation;
- (void)stop;
@end
