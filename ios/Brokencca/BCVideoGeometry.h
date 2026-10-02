#import <UIKit/UIKit.h>

@interface BCVideoGeometry : NSObject
@property(nonatomic, readonly) NSDictionary *configuration;
@property(nonatomic, readonly) CGSize codedSize;
- (instancetype)initWithConfiguration:(NSDictionary *)configuration;
- (CGRect)codedRectInBounds:(CGRect)bounds;
- (CGRect)contentRectInBounds:(CGRect)bounds;
- (BOOL)mapPoint:(CGPoint)point bounds:(CGRect)bounds toVirtual:(CGPoint *)result;
- (CGPoint)circleCenterInBounds:(CGRect)bounds radius:(CGFloat *)radius;
@end
