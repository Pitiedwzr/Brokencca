#import <UIKit/UIKit.h>

@interface BCVideoGeometry : NSObject
@property(nonatomic, readonly) NSDictionary *configuration;
@property(nonatomic, readonly) CGSize codedSize;
@property(nonatomic, readonly) BOOL zoomToPlayfield;
- (instancetype)initWithConfiguration:(NSDictionary *)configuration;
- (instancetype)initWithConfiguration:(NSDictionary *)configuration zoomToPlayfield:(BOOL)zoom;
- (CGRect)codedRectInBounds:(CGRect)bounds;
- (CGRect)contentRectInBounds:(CGRect)bounds;
- (BOOL)mapPoint:(CGPoint)point bounds:(CGRect)bounds toVirtual:(CGPoint *)result;
- (CGPoint)circleCenterInBounds:(CGRect)bounds radius:(CGFloat *)radius;
@end
