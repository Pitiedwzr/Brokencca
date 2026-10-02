#import "BCVideoGeometry.h"
#import "BCVideoLayout.h"

@interface BCVideoGeometry () {
    BCVideoLayout _layout;
}
@end

@implementation BCVideoGeometry
- (instancetype)initWithConfiguration:(NSDictionary *)configuration {
    return [self initWithConfiguration:configuration zoomToPlayfield:NO];
}
- (instancetype)initWithConfiguration:(NSDictionary *)configuration zoomToPlayfield:(BOOL)zoom {
    if ((self = [super init])) {
        _configuration = [configuration copy]; _codedSize = CGSizeMake([configuration[@"codedWidth"] doubleValue], [configuration[@"codedHeight"] doubleValue]);
        _zoomToPlayfield = zoom;
        _layout.codedWidth = _codedSize.width; _layout.codedHeight = _codedSize.height;
        _layout.sourceWidth = [configuration[@"sourceWidth"] doubleValue]; _layout.sourceHeight = [configuration[@"sourceHeight"] doubleValue];
        NSArray *crop=configuration[@"crop"],*content=configuration[@"contentRect"],*circle=configuration[@"circle"];
        for (NSUInteger i=0;i<4;i++) { _layout.crop[i]=[crop[i] doubleValue]; _layout.content[i]=[content[i] doubleValue]; }
        for (NSUInteger i=0;i<3;i++) _layout.circle[i]=[circle[i] doubleValue];
    }
    return self;
}
- (CGRect)codedRectInBounds:(CGRect)bounds {
    BCVideoRect r = BCVideoCodedRect(_layout,bounds.size.width,bounds.size.height,self.zoomToPlayfield);
    return CGRectMake(bounds.origin.x+r.x,bounds.origin.y+r.y,r.width,r.height);
}
- (CGRect)contentRectInBounds:(CGRect)bounds {
    BCVideoRect r=BCVideoContentRect(_layout,BCVideoCodedRect(_layout,bounds.size.width,bounds.size.height,self.zoomToPlayfield));
    return CGRectMake(bounds.origin.x+r.x,bounds.origin.y+r.y,r.width,r.height);
}
- (BOOL)mapPoint:(CGPoint)p bounds:(CGRect)bounds toVirtual:(CGPoint *)result {
    double vx,vy;
    if (!BCVideoMapPoint(_layout,bounds.size.width,bounds.size.height,self.zoomToPlayfield,
        p.x-bounds.origin.x,p.y-bounds.origin.y,&vx,&vy)) return NO;
    *result = CGPointMake(vx,vy);
    return YES;
}
- (CGPoint)circleCenterInBounds:(CGRect)bounds radius:(CGFloat *)radius {
    double cx,cy,r;
    BCVideoCircleInView(_layout,bounds.size.width,bounds.size.height,self.zoomToPlayfield,&cx,&cy,&r);
    *radius = r;
    return CGPointMake(bounds.origin.x+cx,bounds.origin.y+cy);
}
@end
