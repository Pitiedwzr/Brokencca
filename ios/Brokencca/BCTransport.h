#import <Foundation/Foundation.h>

@interface BCTransport : NSObject
@property(nonatomic, copy) void (^statusChanged)(NSString *status, BOOL connected);
- (void)start;
- (void)stop;
- (void)updateTouches:(NSData *)bitmap;
@end
