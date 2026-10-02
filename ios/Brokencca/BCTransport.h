#import <Foundation/Foundation.h>

@interface BCTransport : NSObject
@property(nonatomic, copy) void (^statusChanged)(NSString *status, BOOL connected);
// Runs on the control queue. Video owns its own queue and must not block input here.
@property(nonatomic, copy) void (^videoSessionChanged)(NSData *token);
- (void)start;
- (void)stop;
- (void)updateTouches:(NSData *)bitmap
       eventTimestamp:(NSTimeInterval)eventTimestamp
      callbackStarted:(NSTimeInterval)callbackStarted
             contacts:(NSUInteger)contacts
         changedZones:(NSUInteger)changedZones;
@end
