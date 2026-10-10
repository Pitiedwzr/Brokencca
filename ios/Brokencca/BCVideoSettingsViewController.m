#import "BCVideoSettingsViewController.h"

@interface BCVideoSettingsViewController ()
@property(nonatomic) NSInteger bitrate, activeBitrate;
@property(nonatomic) BOOL automatic;
@property(nonatomic) BCVideoLayoutMode mode;
@property(nonatomic, strong) NSArray<NSNumber *> *bitrates;
@end

@implementation BCVideoSettingsViewController
- (instancetype)initWithBitrate:(NSInteger)bitrate automatic:(BOOL)automatic layout:(BCVideoLayoutMode)mode activeBitrate:(NSInteger)active {
    if ((self=[super initWithStyle:UITableViewStyleInsetGrouped])) {
        _bitrate=bitrate; _automatic=automatic; _mode=mode; _activeBitrate=active;
        NSMutableArray *values=[@[@0,@6000000,@8000000,@10000000,@15000000,@20000000] mutableCopy];
        if (![values containsObject:@(bitrate)]) { [values addObject:@(bitrate)]; [values sortUsingSelector:@selector(compare:)]; }
        _bitrates=[values copy];
    }
    return self;
}
- (void)viewDidLoad {
    [super viewDidLoad]; self.title=@"Video settings";
    self.navigationItem.leftBarButtonItem=[[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemCancel target:self action:@selector(cancel)];
    self.navigationItem.rightBarButtonItem=[[UIBarButtonItem alloc] initWithTitle:@"Apply" style:UIBarButtonItemStyleDone target:self action:@selector(apply)];
}
- (NSInteger)numberOfSectionsInTableView:(UITableView *)tableView { return 3; }
- (NSInteger)tableView:(UITableView *)tableView numberOfRowsInSection:(NSInteger)section { return section==0 ? self.bitrates.count : section==1 ? 1 : 3; }
- (NSString *)tableView:(UITableView *)tableView titleForHeaderInSection:(NSInteger)section {
    return @[@"Preferred bitrate",@"Performance",@"Ring alignment"][section];
}
- (NSString *)tableView:(UITableView *)tableView titleForFooterInSection:(NSInteger)section {
    if (section==0) return [NSString stringWithFormat:@"%@ Changes may briefly pause video. Your preference is saved on this iPad.",
        self.activeBitrate ? [NSString stringWithFormat:@"Active stream: %.1f Mbps.",self.activeBitrate/1000000.0] : @"No active video stream."];
    if (section==1) return @"Reduce bitrate if the connection backs up. 60 fps and low latency remain the priority. Thermal protection is always enabled.";
    return @"Arcade alignment places the game's outer ring at the visible inner edge of the full-size sensor ring. Touch zones and LEDs keep their full size.";
}
- (UITableViewCell *)tableView:(UITableView *)tableView cellForRowAtIndexPath:(NSIndexPath *)path {
    UITableViewCell *cell=[[UITableViewCell alloc] initWithStyle:UITableViewCellStyleDefault reuseIdentifier:nil];
    if (path.section==0) {
        NSInteger bitrate=self.bitrates[path.row].integerValue;
        cell.textLabel.text=bitrate ? [NSString stringWithFormat:@"%.0f Mbps",bitrate/1000000.0] : @"Use host setting (default)";
        cell.accessoryType=bitrate==self.bitrate ? UITableViewCellAccessoryCheckmark : UITableViewCellAccessoryNone;
    } else if (path.section==1) {
        cell.textLabel.text=@"Automatic bitrate reduction";
        UISwitch *toggle=[UISwitch new]; toggle.on=self.automatic;
        toggle.accessibilityLabel=cell.textLabel.text;
        [toggle addTarget:self action:@selector(automaticChanged:) forControlEvents:UIControlEventValueChanged];
        cell.accessoryView=toggle; cell.selectionStyle=UITableViewCellSelectionStyleNone;
    } else {
        cell.textLabel.text=@[@"Full image",@"Full-size ring (default)",@"Arcade alignment"][path.row];
        cell.accessoryType=path.row==self.mode ? UITableViewCellAccessoryCheckmark : UITableViewCellAccessoryNone;
    }
    return cell;
}
- (void)tableView:(UITableView *)tableView didSelectRowAtIndexPath:(NSIndexPath *)path {
    if (path.section==0) self.bitrate=self.bitrates[path.row].integerValue;
    if (path.section==2) self.mode=(BCVideoLayoutMode)path.row;
    [tableView deselectRowAtIndexPath:path animated:YES];
    [tableView reloadSections:[NSIndexSet indexSetWithIndex:path.section] withRowAnimation:UITableViewRowAnimationNone];
}
- (void)automaticChanged:(UISwitch *)toggle { self.automatic=toggle.on; }
- (void)cancel { [self dismissViewControllerAnimated:YES completion:nil]; }
- (void)apply {
    if (self.applied) self.applied(self.bitrate,self.automatic,self.mode);
    [self dismissViewControllerAnimated:YES completion:nil];
}
@end
