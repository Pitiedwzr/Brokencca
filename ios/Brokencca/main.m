#import <UIKit/UIKit.h>
#import "BCTransport.h"
#import "BCTouchGeometry.h"
#import "BCLEDTransport.h"
#import "BCLed.h"
#import "BCVideoTransport.h"
#import "BCVideoView.h"

@interface BCTouchView : UIView
@property(nonatomic, strong) NSMutableSet<UITouch *> *activeTouches;
@property(nonatomic, strong) NSData *bitmap;
@property(nonatomic, strong) NSData *ledPayload;
@property(nonatomic, strong) BCVideoGeometry *videoGeometry;
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
        if (self.videoGeometry) {
            CGPoint virtualPoint;
            if ([self.videoGeometry mapPoint:p bounds:self.bounds toVirtual:&virtualPoint]) BCApplyTouch(virtualPoint.x, virtualPoint.y, 2, 2, bitmap);
        } else BCApplyTouch(p.x, p.y, self.bounds.size.width, self.bounds.size.height, bitmap);
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
    if (self.videoGeometry) { CGFloat calibratedRadius; center = [self.videoGeometry circleCenterInBounds:self.bounds radius:&calibratedRadius]; radius = calibratedRadius; }
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
            UIColor *color = self.videoGeometry ? UIColor.clearColor : [UIColor colorWithWhite:0.08 alpha:1];
            if (leds) {
                int offset = 4 * (BCLedIndexForZone(zone) + half);
                color = [UIColor colorWithRed:leds[offset] / 255.0 green:leds[offset + 1] / 255.0 blue:leds[offset + 2] / 255.0 alpha:self.videoGeometry ? .25 : 1];
            }
            CGContextSetFillColorWithColor(c, (on ? [UIColor.systemCyanColor colorWithAlphaComponent:self.videoGeometry ? .45 : 1] : color).CGColor);
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
@property(nonatomic, strong) BCVideoView *videoView;
@property(nonatomic, strong) BCVideoTransport *videoTransport;
@property(nonatomic, strong) UILabel *status;
@property(nonatomic, strong) UIButton *touchSizeButton;
- (void)toggleTouchSize;
- (void)updateTouchSizeButton;
@end

@implementation BCViewController
- (void)loadView {
    self.view = [[UIView alloc] initWithFrame:CGRectZero]; self.view.backgroundColor = UIColor.blackColor;
    self.videoView = [[BCVideoView alloc] initWithFrame:CGRectZero]; self.touchView = [[BCTouchView alloc] initWithFrame:CGRectZero];
    [self.view addSubview:self.videoView]; [self.view addSubview:self.touchView];
    for (UIView *view in @[self.videoView,self.touchView]) {
        view.translatesAutoresizingMaskIntoConstraints = NO;
        [NSLayoutConstraint activateConstraints:@[[view.topAnchor constraintEqualToAnchor:self.view.topAnchor],
            [view.bottomAnchor constraintEqualToAnchor:self.view.bottomAnchor],[view.leftAnchor constraintEqualToAnchor:self.view.leftAnchor],
            [view.rightAnchor constraintEqualToAnchor:self.view.rightAnchor]]];
    }
}
- (void)viewDidLoad {
    [super viewDidLoad];
    self.transport = [BCTransport new];
    self.ledTransport = [BCLEDTransport new];
    self.videoTransport = [BCVideoTransport new]; self.videoTransport.videoView = self.videoView;
    NSUserDefaults *defaults = NSUserDefaults.standardUserDefaults;
    [defaults registerDefaults:@{@"zoomVideoPlayfield":@YES}];
    self.videoView.zoomToPlayfield = [defaults boolForKey:@"zoomVideoPlayfield"];
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
        [self.status.topAnchor constraintEqualToAnchor:self.view.safeAreaLayoutGuide.topAnchor constant:8],
        [self.status.widthAnchor constraintLessThanOrEqualToAnchor:self.view.widthAnchor multiplier:0.95]
    ]];
    self.touchSizeButton = [UIButton buttonWithType:UIButtonTypeSystem];
    self.touchSizeButton.translatesAutoresizingMaskIntoConstraints = NO;
    self.touchSizeButton.hidden = YES;
    self.touchSizeButton.backgroundColor = [UIColor colorWithWhite:0 alpha:.65];
    self.touchSizeButton.layer.cornerRadius = 8;
    self.touchSizeButton.contentEdgeInsets = UIEdgeInsetsMake(8,12,8,12);
    [self.touchSizeButton setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
    [self.touchSizeButton addTarget:self action:@selector(toggleTouchSize) forControlEvents:UIControlEventTouchUpInside];
    [self.view addSubview:self.touchSizeButton];
    [NSLayoutConstraint activateConstraints:@[
        [self.touchSizeButton.topAnchor constraintEqualToAnchor:self.view.safeAreaLayoutGuide.topAnchor constant:8],
        [self.touchSizeButton.trailingAnchor constraintEqualToAnchor:self.view.safeAreaLayoutGuide.trailingAnchor constant:-8]
    ]];
    [self updateTouchSizeButton];
    __weak BCViewController *weakSelf = self;
    self.transport.videoSessionChanged = ^(NSData *token) {
        [weakSelf.videoTransport setControlToken:token];
        if (!token) dispatch_async(dispatch_get_main_queue(), ^{
            BCTouchView *touch = weakSelf.touchView; [touch clearTouches]; touch.videoGeometry = nil;
            touch.opaque = YES; touch.backgroundColor = [UIColor colorWithWhite:0.035 alpha:1]; [touch setNeedsDisplay];
            weakSelf.touchSizeButton.hidden = YES;
        });
    };
    self.videoView.geometryChanged = ^(BCVideoGeometry *geometry) {
        BCTouchView *touch = weakSelf.touchView;
        [touch clearTouches]; touch.videoGeometry = geometry; touch.opaque = NO; touch.backgroundColor = UIColor.clearColor;
        [touch setNeedsDisplay]; weakSelf.status.text = @"Wired video · 60 fps target";
        weakSelf.touchSizeButton.hidden = NO;
    };
    self.videoTransport.statusChanged = ^(NSString *status) { weakSelf.status.text = status; };
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
        s.status.text = status;
        [s.touchView clearTouches];
    };
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(pause) name:UIApplicationWillResignActiveNotification object:nil];
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(resume) name:UIApplicationDidBecomeActiveNotification object:nil];
    [self resume];
}
- (void)updateTouchSizeButton {
    [self.touchSizeButton setTitle:self.videoView.zoomToPlayfield ? @"Full-size ring" : @"Full image" forState:UIControlStateNormal];
    self.touchSizeButton.accessibilityLabel = @"Video layout";
    self.touchSizeButton.accessibilityValue = self.videoView.zoomToPlayfield ? @"Game ring fills the controller" : @"Entire game image";
    self.touchSizeButton.accessibilityHint = @"Switches between the full-size game ring and the entire game image; touch stays aligned";
}
- (void)toggleTouchSize {
    [self.touchView clearTouches];
    self.videoView.zoomToPlayfield = !self.videoView.zoomToPlayfield;
    [NSUserDefaults.standardUserDefaults setBool:self.videoView.zoomToPlayfield forKey:@"zoomVideoPlayfield"];
    [self updateTouchSizeButton]; [self.touchView setNeedsDisplay];
}
- (void)pause { [self.touchView clearTouches]; [self.transport stop]; [self.ledTransport stop]; [self.videoTransport stop]; [self.videoView setPaused:YES]; UIApplication.sharedApplication.idleTimerDisabled = NO; }
- (void)resume { UIApplication.sharedApplication.idleTimerDisabled = YES; [self.videoTransport start]; [self.transport start]; [self.ledTransport start]; [self.videoView setPaused:NO]; }
- (void)viewWillTransitionToSize:(CGSize)size withTransitionCoordinator:(id<UIViewControllerTransitionCoordinator>)coordinator {
    [self.touchView clearTouches];
    [super viewWillTransitionToSize:size withTransitionCoordinator:coordinator];
    [coordinator animateAlongsideTransition:nil completion:^(id<UIViewControllerTransitionCoordinatorContext> context) { [self.touchView setNeedsDisplay]; }];
}
- (BOOL)prefersStatusBarHidden { return YES; }
- (BOOL)prefersHomeIndicatorAutoHidden { return YES; }
- (UIRectEdge)preferredScreenEdgesDeferringSystemGestures { return UIRectEdgeAll; }
- (void)dealloc { [NSNotificationCenter.defaultCenter removeObserver:self]; [self.transport stop]; [self.ledTransport stop]; [self.videoTransport stop]; }
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
