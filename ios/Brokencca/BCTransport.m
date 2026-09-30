#import "BCTransport.h"
#import "BCWire.h"
#import <Network/Network.h>

@interface BCTransport ()
@property(nonatomic, strong) dispatch_queue_t queue;
@property(nonatomic, strong) nw_listener_t listener;
@property(nonatomic, strong) nw_connection_t connection;
@property(nonatomic, strong) dispatch_source_t timer;
@property(nonatomic, strong) NSMutableData *received;
@property(nonatomic, strong) NSData *bitmap;
@property(nonatomic) uint32_t sequence;
@property(nonatomic) NSUInteger pendingWrites;
@property(nonatomic) BOOL ready;
@end

@implementation BCTransport
- (instancetype)init {
    if ((self = [super init])) {
        _queue = dispatch_queue_create("org.brokencca.transport", DISPATCH_QUEUE_SERIAL);
        _bitmap = [NSMutableData dataWithLength:30];
    }
    return self;
}
- (void)notify:(NSString *)status connected:(BOOL)connected {
    dispatch_async(dispatch_get_main_queue(), ^{
        if (self.statusChanged) self.statusChanged(status, connected);
    });
}
- (void)start {
    dispatch_async(self.queue, ^{
        if (self.listener) return;
        nw_parameters_t params = nw_parameters_create_secure_tcp(NW_PARAMETERS_DISABLE_PROTOCOL,
            ^(nw_protocol_options_t options) { nw_tcp_options_set_no_delay(options, true); });
        // usbmux connects locally on the device; do not advertise a Wi-Fi listener.
        nw_parameters_set_local_endpoint(params, nw_endpoint_create_host("127.0.0.1", "24864"));
        nw_listener_t listener = nw_listener_create(params);
        if (!listener) { [self notify:@"Could not create USB listener" connected:NO]; return; }
        self.listener = listener;
        nw_listener_set_queue(listener, self.queue);
        __weak BCTransport *weakSelf = self;
        nw_listener_set_state_changed_handler(listener, ^(nw_listener_state_t state, nw_error_t error) {
            BCTransport *s = weakSelf;
            if (!s || s.listener != listener) return;
            if (state == nw_listener_state_ready) [s notify:@"Connect USB and start the Windows host" connected:NO];
            if (state == nw_listener_state_failed) {
                [s stopInternal];
                [s notify:@"USB listener failed; reopen the app" connected:NO];
            }
        });
        nw_listener_set_new_connection_handler(listener, ^(nw_connection_t connection) {
            BCTransport *s = weakSelf;
            if (!s || s.listener != listener || s.connection) { nw_connection_cancel(connection); return; }
            [s accept:connection];
        });
        nw_listener_start(listener);
    });
}
- (void)accept:(nw_connection_t)connection {
    self.connection = connection;
    self.received = [NSMutableData data];
    self.bitmap = [NSMutableData dataWithLength:30];
    self.sequence = 0;
    self.pendingWrites = 0;
    self.ready = NO;
    nw_connection_set_queue(connection, self.queue);
    __weak BCTransport *weakSelf = self;
    nw_connection_set_state_changed_handler(connection, ^(nw_connection_state_t state, nw_error_t error) {
        BCTransport *s = weakSelf;
        if (!s || s.connection != connection) return;
        if (state == nw_connection_state_ready) [s receive:connection];
        if (state == nw_connection_state_failed || state == nw_connection_state_cancelled)
            [s disconnect:@"Disconnected — waiting for Windows"];
    });
    nw_connection_start(connection);
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC), self.queue, ^{
        BCTransport *s = weakSelf;
        if (s.connection == connection && !s.ready) [s disconnect:@"Handshake timed out"];
    });
}
- (void)receive:(nw_connection_t)connection {
    __weak BCTransport *weakSelf = self;
    nw_connection_receive(connection, 1, 4096, ^(dispatch_data_t content, nw_content_context_t context, bool complete, nw_error_t error) {
        BCTransport *s = weakSelf;
        if (!s || s.connection != connection) return;
        if (content) {
            dispatch_data_apply(content, ^bool(dispatch_data_t region, size_t offset, const void *buffer, size_t size) {
                [s.received appendBytes:buffer length:size]; return true;
            });
            [s parse];
        }
        if (s.connection != connection) return;
        if (error || complete) { [s disconnect:@"Disconnected — waiting for Windows"]; return; }
        [s receive:connection];
    });
}
- (void)parse {
    // Input-only v1: host sends precisely one HELLO. Future control messages require explicit support.
    if (self.received.length < BCHeaderSize) return;
    const uint8_t *p = self.received.bytes;
    if (self.ready || memcmp(p, "BCCA", 4) || p[4] != 1 || p[5] != BCHello || p[6] || p[7] || BCGet32(p + 8) != 4) {
        [self disconnect:@"Incompatible host protocol"]; return;
    }
    if (self.received.length < 28) return;
    const uint8_t hello[] = {240, 0, 30, 0};
    if (memcmp(p + BCHeaderSize, hello, 4) || self.received.length != 28) {
        [self disconnect:@"Unsupported controller layout"]; return;
    }
    self.received.length = 0;
    self.ready = YES;
    [self send:BCHello payload:[NSData dataWithBytes:hello length:4]];
    [self send:BCTouch payload:self.bitmap];
    [self notify:@"Connected · wired input" connected:YES];
    self.timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, self.queue);
    dispatch_source_set_timer(self.timer, dispatch_time(DISPATCH_TIME_NOW, 100 * NSEC_PER_MSEC),
        100 * NSEC_PER_MSEC, 5 * NSEC_PER_MSEC);
    __weak BCTransport *weakSelf = self;
    dispatch_source_set_event_handler(self.timer, ^{
        BCTransport *s = weakSelf;
        if (s.ready) [s send:BCTouch payload:s.bitmap];
    });
    dispatch_resume(self.timer);
}
- (void)send:(uint8_t)type payload:(NSData *)payload {
    if (!self.connection || !self.ready) return;
    if (self.pendingWrites >= 64) { [self disconnect:@"USB stalled — reconnecting"]; return; }
    uint8_t header[BCHeaderSize];
    uint64_t now = (uint64_t)(NSProcessInfo.processInfo.systemUptime * 1000000.0);
    BCHeader(header, type, (uint32_t)payload.length, self.sequence++, now);
    NSMutableData *packet = [NSMutableData dataWithBytes:header length:sizeof(header)];
    [packet appendData:payload];
    // Destructor block retains packet until Network.framework has finished using its storage.
    dispatch_data_t data = dispatch_data_create(packet.bytes, packet.length, self.queue, ^{ (void)packet; });
    nw_connection_t connection = self.connection;
    self.pendingWrites++;
    __weak BCTransport *weakSelf = self;
    nw_connection_send(connection, data, NW_CONNECTION_DEFAULT_MESSAGE_CONTEXT, true, ^(nw_error_t error) {
        BCTransport *s = weakSelf;
        if (!s || s.connection != connection) return;
        s.pendingWrites--;
        if (error) [s disconnect:@"Disconnected — waiting for Windows"];
    });
}
- (void)updateTouches:(NSData *)bitmap {
    if (bitmap.length != 30) return;
    NSData *snapshot = [bitmap copy];
    dispatch_async(self.queue, ^{
        if (!self.ready || [self.bitmap isEqualToData:snapshot]) return;
        self.bitmap = snapshot;
        [self send:BCTouch payload:snapshot];
    });
}
- (void)disconnect:(NSString *)reason {
    if (self.timer) { dispatch_source_cancel(self.timer); self.timer = nil; }
    nw_connection_t connection = self.connection;
    self.connection = nil;
    self.ready = NO;
    self.received = nil;
    self.bitmap = [NSMutableData dataWithLength:30];
    if (connection) {
        nw_connection_set_state_changed_handler(connection, NULL);
        nw_connection_cancel(connection);
    }
    [self notify:reason connected:NO];
}
- (void)stopInternal {
    [self disconnect:@"Paused"];
    if (self.listener) {
        nw_listener_set_new_connection_handler(self.listener, NULL);
        nw_listener_set_state_changed_handler(self.listener, NULL);
        nw_listener_cancel(self.listener);
        self.listener = nil;
    }
}
- (void)stop {
    // Closing the connection triggers host-side reset even if a final reset packet cannot be delivered.
    dispatch_async(self.queue, ^{ [self stopInternal]; });
}
@end
