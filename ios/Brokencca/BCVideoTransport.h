#import <Foundation/Foundation.h>
#import "BCVideoView.h"

@interface BCVideoTransport : NSObject
@property(nonatomic, weak) BCVideoView *videoView;
@property(nonatomic, copy) void (^statusChanged)(NSString *status);
@property(nonatomic, copy) void (^activeBitrateChanged)(NSInteger bitrate);
- (void)setControlToken:(NSData *)token;
- (void)setPreferredBitrate:(NSInteger)bitrate automaticReduction:(BOOL)automatic;
- (void)start;
- (void)stop;
@end
