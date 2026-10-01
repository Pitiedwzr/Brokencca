#import "BCLEDTransport.h"
#import "BCLed.h"
#import <Network/Network.h>

@interface BCLEDTransport ()
@property(nonatomic, strong) dispatch_queue_t queue;
@property(nonatomic, strong) nw_listener_t listener;
@property(nonatomic, strong) nw_connection_t connection;
@property(nonatomic, strong) dispatch_source_t timer;
@property(nonatomic, strong) NSMutableData *received;
@property(nonatomic, strong) NSData *latest;
@property(nonatomic) BOOL displayPending;
@property(nonatomic) BOOL dirty;
@property(nonatomic) BOOL haveSequence;
@property(nonatomic) uint32_t sequence;
@property(nonatomic) NSTimeInterval lastFrame;
@property(nonatomic) NSTimeInterval lastReport;
@property(nonatomic) NSUInteger framesReceived;
@property(nonatomic) NSUInteger framesDisplayed;
@end

@implementation BCLEDTransport
- (instancetype)init {
    if ((self = [super init])) _queue = dispatch_queue_create("org.brokencca.leds", DISPATCH_QUEUE_SERIAL);
    return self;
}
- (void)start {
    dispatch_async(self.queue, ^{
        if (self.listener) return;
        nw_parameters_t params = nw_parameters_create_secure_tcp(NW_PARAMETERS_DISABLE_PROTOCOL,
            ^(nw_protocol_options_t options) { nw_tcp_options_set_no_delay(options, true); });
        nw_parameters_set_local_endpoint(params, nw_endpoint_create_host("127.0.0.1", "24866"));
        nw_listener_t listener = nw_listener_create(params);
        if (!listener) return; // LEDs are optional; never stop the touch listener.
        self.listener = listener;
        nw_listener_set_queue(listener, self.queue);
        __weak BCLEDTransport *weakSelf = self;
        nw_listener_set_state_changed_handler(listener, ^(nw_listener_state_t state, nw_error_t error) {
            BCLEDTransport *s = weakSelf;
            if (s.listener == listener && state == nw_listener_state_failed) [s stopInternal];
        });
        nw_listener_set_new_connection_handler(listener, ^(nw_connection_t connection) {
            BCLEDTransport *s = weakSelf;
            if (!s || s.listener != listener || s.connection) { nw_connection_cancel(connection); return; }
            s.connection = connection; s.received = [NSMutableData data]; s.haveSequence = NO;
            s.lastFrame = NSProcessInfo.processInfo.systemUptime;
            nw_connection_set_queue(connection, s.queue);
            nw_connection_set_state_changed_handler(connection, ^(nw_connection_state_t state, nw_error_t error) {
                BCLEDTransport *current = weakSelf;
                if (!current || current.connection != connection) return;
                if (state == nw_connection_state_ready) [current receive:connection];
                if (state == nw_connection_state_failed || state == nw_connection_state_cancelled) [current disconnect];
            });
            nw_connection_start(connection);
        });
        nw_listener_start(listener);
        self.timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, self.queue);
        dispatch_source_set_timer(self.timer, DISPATCH_TIME_NOW, NSEC_PER_SEC / 30, 2 * NSEC_PER_MSEC);
        dispatch_source_set_event_handler(self.timer, ^{
            BCLEDTransport *s = weakSelf;
            if (!s) return;
            NSTimeInterval now = NSProcessInfo.processInfo.systemUptime;
            if (s.connection && now - s.lastFrame > 1.0) [s disconnect];
            if (now - s.lastReport >= 1.0 && (s.connection || s.framesReceived)) {
                NSLog(@"BCCA_LED received=%lu displayed=%lu connected=%d age_ms=%.1f",
                    (unsigned long)s.framesReceived, (unsigned long)s.framesDisplayed,
                    s.connection != nil, s.connection ? (now - s.lastFrame) * 1000.0 : 0.0);
                s.framesReceived = s.framesDisplayed = 0; s.lastReport = now;
            }
            if (!s.dirty || s.displayPending) return;
            s.dirty = NO; s.displayPending = YES;
            NSData *snapshot = s.latest;
            dispatch_async(dispatch_get_main_queue(), ^{
                if (s.frameChanged) s.frameChanged(snapshot);
                dispatch_async(s.queue, ^{ s.displayPending = NO; if (snapshot) s.framesDisplayed++; });
            });
        });
        dispatch_resume(self.timer);
    });
}
- (void)receive:(nw_connection_t)connection {
    __weak BCLEDTransport *weakSelf = self;
    nw_connection_receive(connection, 1, 4096, ^(dispatch_data_t content, nw_content_context_t context, bool complete, nw_error_t error) {
        BCLEDTransport *s = weakSelf;
        if (!s || s.connection != connection) return;
        if (content) {
            dispatch_data_apply(content, ^bool(dispatch_data_t region, size_t offset, const void *buffer, size_t size) {
                [s.received appendBytes:buffer length:size]; return true;
            });
            while (s.received.length >= BCLedHeaderSize) {
                const uint8_t *p = s.received.bytes;
                if (!BCValidLedHeader(p)) { [s disconnect]; return; }
                if (s.received.length < BCLedPacketSize) break;
                uint32_t sequence = BCGet32(p + 12);
                if (s.haveSequence && (int32_t)(sequence - s.sequence) <= 0) { [s disconnect]; return; }
                s.sequence = sequence; s.haveSequence = YES;
                s.framesReceived++;
                s.latest = [NSData dataWithBytes:p + BCLedHeaderSize length:BCLedPayloadSize];
                s.dirty = YES; s.lastFrame = NSProcessInfo.processInfo.systemUptime;
                [s.received replaceBytesInRange:NSMakeRange(0, BCLedPacketSize) withBytes:NULL length:0];
            }
        }
        if (s.connection != connection) return;
        if (error || complete) { [s disconnect]; return; }
        [s receive:connection];
    });
}
- (void)disconnect {
    nw_connection_t connection = self.connection; self.connection = nil; self.received = nil;
    self.latest = nil; self.dirty = YES;
    if (connection) { nw_connection_set_state_changed_handler(connection, NULL); nw_connection_cancel(connection); }
}
- (void)stopInternal {
    [self disconnect];
    if (self.timer) { dispatch_source_cancel(self.timer); self.timer = nil; }
    if (self.listener) {
        nw_listener_set_new_connection_handler(self.listener, NULL);
        nw_listener_set_state_changed_handler(self.listener, NULL);
        nw_listener_cancel(self.listener); self.listener = nil;
    }
    // The timer is stopped: explicitly clear LEDs after any already-queued display.
    dispatch_async(dispatch_get_main_queue(), ^{ if (self.frameChanged) self.frameChanged(nil); });
}
- (void)stop { dispatch_async(self.queue, ^{ [self stopInternal]; }); }
@end
