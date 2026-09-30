#import <Foundation/Foundation.h>

@interface BCTransport : NSObject
@property(nonatomic, copy) void (^statusChanged)(NSString *status, BOOL connected);
- (void)start;
- (void)stop;
- (void)updateTouches:(NSData *)bitmap
       eventTimestamp:(NSTimeInterval)eventTimestamp
      callbackStarted:(NSTimeInterval)callbackStarted
             contacts:(NSUInteger)contacts
         changedZones:(NSUInteger)changedZones;
@end
