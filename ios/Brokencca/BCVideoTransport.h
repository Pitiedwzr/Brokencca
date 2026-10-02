#import <Foundation/Foundation.h>
#import "BCVideoView.h"

@interface BCVideoTransport : NSObject
@property(nonatomic, weak) BCVideoView *videoView;
@property(nonatomic, copy) void (^statusChanged)(NSString *status);
- (void)setControlToken:(NSData *)token;
- (void)start;
- (void)stop;
@end
