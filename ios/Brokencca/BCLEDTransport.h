#import <Foundation/Foundation.h>

@interface BCLEDTransport : NSObject
// Main queue, latest complete 1924-byte payload; nil on disconnect/stale stream.
@property(nonatomic, copy) void (^frameChanged)(NSData *payload);
- (void)start;
- (void)stop;
@end
