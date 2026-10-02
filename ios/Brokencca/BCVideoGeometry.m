#import "BCVideoGeometry.h"

@implementation BCVideoGeometry
- (instancetype)initWithConfiguration:(NSDictionary *)configuration {
    if ((self = [super init])) { _configuration = [configuration copy]; _codedSize = CGSizeMake([configuration[@"codedWidth"] doubleValue], [configuration[@"codedHeight"] doubleValue]); }
    return self;
}
- (CGRect)codedRectInBounds:(CGRect)bounds {
    double scale = MIN(bounds.size.width / self.codedSize.width, bounds.size.height / self.codedSize.height);
    return CGRectMake((bounds.size.width - self.codedSize.width * scale)/2,
        (bounds.size.height - self.codedSize.height * scale)/2, self.codedSize.width * scale, self.codedSize.height * scale);
}
- (CGRect)contentRectInBounds:(CGRect)bounds {
    CGRect fit = [self codedRectInBounds:bounds]; NSArray *content = self.configuration[@"contentRect"];
    double scale = fit.size.width / self.codedSize.width;
    return CGRectMake(fit.origin.x + [content[0] doubleValue]*scale, fit.origin.y + [content[1] doubleValue]*scale,
        [content[2] doubleValue]*scale, [content[3] doubleValue]*scale);
}
- (BOOL)mapPoint:(CGPoint)p bounds:(CGRect)bounds toVirtual:(CGPoint *)result {
    CGRect content = [self contentRectInBounds:bounds];
    if (content.size.width <= 0 || content.size.height <= 0 || p.x < CGRectGetMinX(content) || p.x >= CGRectGetMaxX(content) || p.y < CGRectGetMinY(content) || p.y >= CGRectGetMaxY(content)) return NO;
    NSArray *crop = self.configuration[@"crop"], *circle = self.configuration[@"circle"];
    double w = [self.configuration[@"sourceWidth"] doubleValue], h = [self.configuration[@"sourceHeight"] doubleValue];
    double x = ([crop[0] doubleValue] + (p.x-content.origin.x)/content.size.width * [crop[2] doubleValue])*w;
    double y = ([crop[1] doubleValue] + (p.y-content.origin.y)/content.size.height * [crop[3] doubleValue])*h;
    double r = [circle[2] doubleValue]*MIN(w,h);
    *result = CGPointMake((x-[circle[0] doubleValue]*w)/r+1, (y-[circle[1] doubleValue]*h)/r+1);
    return YES;
}
- (CGPoint)circleCenterInBounds:(CGRect)bounds radius:(CGFloat *)radius {
    CGRect content = [self contentRectInBounds:bounds];
    NSArray *crop = self.configuration[@"crop"], *circle = self.configuration[@"circle"];
    double w = [self.configuration[@"sourceWidth"] doubleValue], h = [self.configuration[@"sourceHeight"] doubleValue];
    *radius = [circle[2] doubleValue]*MIN(w,h)*MIN(content.size.width/([crop[2] doubleValue]*w), content.size.height/([crop[3] doubleValue]*h));
    return CGPointMake(content.origin.x+([circle[0] doubleValue]-[crop[0] doubleValue])/[crop[2] doubleValue]*content.size.width,
        content.origin.y+([circle[1] doubleValue]-[crop[1] doubleValue])/[crop[3] doubleValue]*content.size.height);
}
@end
