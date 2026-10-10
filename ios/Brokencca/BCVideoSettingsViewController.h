#import <UIKit/UIKit.h>
#import "BCVideoLayout.h"

@interface BCVideoSettingsViewController : UITableViewController
@property(nonatomic, copy) void (^applied)(NSInteger bitrate, BOOL automatic, BCVideoLayoutMode mode);
- (instancetype)initWithBitrate:(NSInteger)bitrate automatic:(BOOL)automatic layout:(BCVideoLayoutMode)mode activeBitrate:(NSInteger)active;
@end
