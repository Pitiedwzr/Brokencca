#import <UIKit/UIKit.h>
#import "BCTransport.h"
#import "BCTouchGeometry.h"
#import "BCLEDTransport.h"
#import "BCLed.h"

@interface BCTouchView : UIView
@property(nonatomic, strong) NSMutableSet<UITouch *> *activeTouches;
@property(nonatomic, strong) NSData *bitmap;
@property(nonatomic, strong) NSData *ledPayload;
@property(nonatomic, copy) void (^changed)(NSData *bitmap, NSTimeInterval eventTimestamp,
    NSTimeInterval callbackStarted, NSUInteger contacts, NSUInteger changedZones);
- (void)clearTouches;
@end

@implementation BCTouchView
- (instancetype)initWithFrame:(CGRect)frame {
    if ((self = [super initWithFrame:frame])) {
        self.multipleTouchEnabled = YES;
        self.backgroundColor = [UIColor colorWithWhite:0.035 alpha:1];
        self.activeTouches = [NSMutableSet set];
        self.bitmap = [NSMutableData dataWithLength:30];
        self.contentMode = UIViewContentModeRedraw;
    }
    return self;
}
- (void)publishEvent:(UIEvent *)event callbackStarted:(NSTimeInterval)callbackStarted {
    uint8_t bitmap[30] = {0};
    for (UITouch *touch in self.activeTouches) {
        CGPoint p = [touch locationInView:self];
        BCApplyTouch(p.x, p.y, self.bounds.size.width, self.bounds.size.height, bitmap);
    }
    NSData *state = [NSData dataWithBytes:bitmap length:30];
    NSUInteger changedZones = 0;
    const uint8_t *old = self.bitmap.bytes;
    for (NSUInteger i = 0; i < 30; i++) changedZones += __builtin_popcount((unsigned)(old[i] ^ bitmap[i]));
    if (changedZones) {
        self.bitmap = state;
        [self setNeedsDisplay];
    }
    if (self.changed) self.changed(state, event ? event.timestamp : callbackStarted,
        callbackStarted, self.activeTouches.count, changedZones);
}
- (void)clearTouches { [self.activeTouches removeAllObjects]; NSTimeInterval now = NSProcessInfo.processInfo.systemUptime; [self publishEvent:nil callbackStarted:now]; }
- (void)touchesBegan:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { NSTimeInterval now = NSProcessInfo.processInfo.systemUptime; [self.activeTouches unionSet:touches]; [self publishEvent:event callbackStarted:now]; }
- (void)touchesMoved:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { NSTimeInterval now = NSProcessInfo.processInfo.systemUptime; [self publishEvent:event callbackStarted:now]; }
- (void)touchesEnded:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { NSTimeInterval now = NSProcessInfo.processInfo.systemUptime; [self.activeTouches minusSet:touches]; [self publishEvent:event callbackStarted:now]; }
- (void)touchesCancelled:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { NSTimeInterval now = NSProcessInfo.processInfo.systemUptime; [self.activeTouches minusSet:touches]; [self publishEvent:event callbackStarted:now]; }
- (void)drawRect:(CGRect)rect {
    CGContextRef c = UIGraphicsGetCurrentContext();
    CGPoint center = CGPointMake(CGRectGetMidX(self.bounds), CGRectGetMidY(self.bounds));
    double radius = fmin(self.bounds.size.width, self.bounds.size.height) / 2;
    const uint8_t *bits = self.bitmap.bytes;
    const uint8_t *leds = self.ledPayload.length == BCLedPayloadSize ? (const uint8_t *)self.ledPayload.bytes + 4 : NULL;
    for (int side = 0; side < 2; side++) for (int ring = 0; ring < 4; ring++) for (int sector = 0; sector < 30; sector++) {
        int zone = side * 120 + ring * 30 + sector;
        double start = side == 0 ? -M_PI_2 + M_PI / 30 * sector : 3 * M_PI_2 - M_PI / 30 * sector;
        double end = start + (side == 0 ? 1 : -1) * M_PI / 30;
        double inner = radius * (0.6 + ring * 0.1), outer = radius * (0.7 + ring * 0.1);
        BOOL on = (bits[zone / 8] & (1 << (zone % 8))) != 0;
        int parts = leds ? 2 : 1;
        for (int half = 0; half < parts; half++) {
            double a = start + (end - start) * half / parts, b = start + (end - start) * (half + 1) / parts;
            CGContextBeginPath(c);
            CGContextAddArc(c, center.x, center.y, inner, a, b, side != 0);
            CGContextAddArc(c, center.x, center.y, outer, b, a, side == 0);
            CGContextClosePath(c);
            UIColor *color = [UIColor colorWithWhite:0.08 alpha:1];
            if (leds) {
                int offset = 4 * (BCLedIndexForZone(zone) + half);
                color = [UIColor colorWithRed:leds[offset] / 255.0 green:leds[offset + 1] / 255.0 blue:leds[offset + 2] / 255.0 alpha:1];
            }
            CGContextSetFillColorWithColor(c, (on ? UIColor.systemCyanColor : color).CGColor);
            CGContextSetStrokeColorWithColor(c, [UIColor colorWithWhite:0.25 alpha:1].CGColor);
            CGContextDrawPath(c, kCGPathFillStroke);
        }
    }
}
@end

@interface BCViewController : UIViewController
@property(nonatomic, strong) BCTransport *transport;
@property(nonatomic, strong) BCLEDTransport *ledTransport;
@property(nonatomic, strong) BCTouchView *touchView;
@property(nonatomic, strong) UILabel *status;
@end

@implementation BCViewController
- (void)loadView { self.touchView = [[BCTouchView alloc] initWithFrame:CGRectZero]; self.view = self.touchView; }
- (void)viewDidLoad {
    [super viewDidLoad];
    self.transport = [BCTransport new];
    self.ledTransport = [BCLEDTransport new];
    self.status = [UILabel new];
    self.status.translatesAutoresizingMaskIntoConstraints = NO;
    self.status.textColor = UIColor.whiteColor;
    self.status.font = [UIFont systemFontOfSize:15 weight:UIFontWeightMedium];
    self.status.textAlignment = NSTextAlignmentCenter;
    self.status.numberOfLines = 0;
    self.status.userInteractionEnabled = NO;
    [self.view addSubview:self.status];
    [NSLayoutConstraint activateConstraints:@[
        [self.status.centerXAnchor constraintEqualToAnchor:self.view.centerXAnchor],
        [self.status.centerYAnchor constraintEqualToAnchor:self.view.centerYAnchor],
        [self.status.widthAnchor constraintLessThanOrEqualToAnchor:self.view.widthAnchor multiplier:0.5]
    ]];
    __weak BCViewController *weakSelf = self;
    self.ledTransport.frameChanged = ^(NSData *payload) {
        weakSelf.touchView.ledPayload = payload;
        [weakSelf.touchView setNeedsDisplay];
    };
    self.touchView.changed = ^(NSData *bitmap, NSTimeInterval eventTimestamp,
        NSTimeInterval callbackStarted, NSUInteger contacts, NSUInteger changedZones) {
        [weakSelf.transport updateTouches:bitmap eventTimestamp:eventTimestamp
            callbackStarted:callbackStarted contacts:contacts changedZones:changedZones];
    };
    self.transport.statusChanged = ^(NSString *status, BOOL connected) {
        BCViewController *s = weakSelf;
        s.status.text = [status stringByAppendingString:@"\nInput prototype · use the PC display"];
        [s.touchView clearTouches];
    };
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(pause) name:UIApplicationWillResignActiveNotification object:nil];
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(resume) name:UIApplicationDidBecomeActiveNotification object:nil];
    [self resume];
}
- (void)pause { [self.touchView clearTouches]; [self.transport stop]; [self.ledTransport stop]; UIApplication.sharedApplication.idleTimerDisabled = NO; }
- (void)resume { UIApplication.sharedApplication.idleTimerDisabled = YES; [self.transport start]; [self.ledTransport start]; }
- (void)viewWillTransitionToSize:(CGSize)size withTransitionCoordinator:(id<UIViewControllerTransitionCoordinator>)coordinator {
    [self.touchView clearTouches];
    [super viewWillTransitionToSize:size withTransitionCoordinator:coordinator];
}
- (BOOL)prefersStatusBarHidden { return YES; }
- (BOOL)prefersHomeIndicatorAutoHidden { return YES; }
- (UIRectEdge)preferredScreenEdgesDeferringSystemGestures { return UIRectEdgeAll; }
- (void)dealloc { [NSNotificationCenter.defaultCenter removeObserver:self]; [self.transport stop]; [self.ledTransport stop]; }
@end

@interface BCAppDelegate : UIResponder <UIApplicationDelegate>
@property(nonatomic, strong) UIWindow *window;
@end
@implementation BCAppDelegate
- (BOOL)application:(UIApplication *)application didFinishLaunchingWithOptions:(NSDictionary *)options {
    self.window = [[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
    self.window.rootViewController = [BCViewController new];
    [self.window makeKeyAndVisible];
    return YES;
}
@end

int main(int argc, char *argv[]) {
    @autoreleasepool { return UIApplicationMain(argc, argv, nil, NSStringFromClass(BCAppDelegate.class)); }
}
