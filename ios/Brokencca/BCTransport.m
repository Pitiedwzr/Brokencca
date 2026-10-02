#import "BCTransport.h"
#import "BCWire.h"
#import <Network/Network.h>
#import <math.h>

static double BCPercentile(NSArray<NSNumber *> *values, double fraction) {
    if (!values.count) return 0;
    NSArray<NSNumber *> *sorted = [values sortedArrayUsingSelector:@selector(compare:)];
    NSUInteger index = (NSUInteger)ceil(fraction * sorted.count) - 1;
    return sorted[MIN(index, sorted.count - 1)].doubleValue;
}

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
@property(nonatomic) uint8_t protocolVersion;
@property(nonatomic, strong) NSData *videoSessionToken;
@property(nonatomic, strong) NSMutableArray<NSNumber *> *pendingWriteTimes;
@property(nonatomic) NSUInteger callbacks;
@property(nonatomic) NSUInteger contactTotal;
@property(nonatomic) NSUInteger contactMax;
@property(nonatomic) NSUInteger changedZoneTotal;
@property(nonatomic) NSUInteger pendingHighWater;
@property(nonatomic) NSUInteger sendCompletions;
@property(nonatomic, strong) NSMutableArray<NSNumber *> *eventToEnqueueSamples;
@property(nonatomic, strong) NSMutableArray<NSNumber *> *callbackToEnqueueSamples;
@property(nonatomic, strong) NSMutableArray<NSNumber *> *sendCompletionSamples;
@property(nonatomic) NSTimeInterval lastDiagnostics;
@end

@implementation BCTransport
- (instancetype)init {
    if ((self = [super init])) {
        _queue = dispatch_queue_create("org.brokencca.transport", DISPATCH_QUEUE_SERIAL);
        _bitmap = [NSMutableData dataWithLength:30];
        _pendingWriteTimes = [NSMutableArray array];
        _eventToEnqueueSamples = [NSMutableArray array];
        _callbackToEnqueueSamples = [NSMutableArray array];
        _sendCompletionSamples = [NSMutableArray array];
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
    [self.pendingWriteTimes removeAllObjects];
    self.lastDiagnostics = NSProcessInfo.processInfo.systemUptime;
    self.ready = NO;
    self.protocolVersion = 1;
    self.videoSessionToken = nil;
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
    if (self.received.length < BCHeaderSize) return;
    const uint8_t *p = self.received.bytes;
    uint8_t version = p[4];
    uint32_t payloadLength = BCGet32(p + 8);
    if (self.ready || memcmp(p, "BCCA", 4) || p[5] != BCHello || p[6] || p[7] ||
        !((version == 1 && payloadLength == 4) || (version == 2 && payloadLength == 24))) {
        [self disconnect:@"Incompatible host protocol"]; return;
    }
    NSUInteger total = BCHeaderSize + payloadLength;
    if (self.received.length < total) return;
    if (!BCValidHello(p, total) || self.received.length != total) {
        [self disconnect:@"Unsupported controller layout"]; return;
    }
    NSData *hello = [NSData dataWithBytes:p + BCHeaderSize length:payloadLength];
    self.protocolVersion = version;
    self.videoSessionToken = version == 2 ? [NSData dataWithBytes:p + BCHeaderSize + 8 length:16] : nil;
    if (self.videoSessionChanged) self.videoSessionChanged(self.videoSessionToken);
    self.received.length = 0;
    self.ready = YES;
    [self send:BCHello payload:hello];
    // Both protocol versions start released, even if fingers touched the waiting screen.
    self.bitmap = [NSMutableData dataWithLength:30];
    [self send:BCTouch payload:self.bitmap];
    [self notify:@"Connected · wired input" connected:YES];
    self.timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, self.queue);
    dispatch_source_set_timer(self.timer, dispatch_time(DISPATCH_TIME_NOW, 100 * NSEC_PER_MSEC),
        100 * NSEC_PER_MSEC, 5 * NSEC_PER_MSEC);
    __weak BCTransport *weakSelf = self;
    dispatch_source_set_event_handler(self.timer, ^{
        BCTransport *s = weakSelf;
        if (s.ready) {
            [s send:BCTouch payload:s.bitmap];
            [s reportDiagnosticsIfDue];
        }
    });
    dispatch_resume(self.timer);
}
- (void)send:(uint8_t)type payload:(NSData *)payload {
    if (!self.connection || !self.ready) return;
    if (self.pendingWrites >= 64) { [self disconnect:@"USB stalled — reconnecting"]; return; }
    uint8_t header[BCHeaderSize];
    uint64_t now = (uint64_t)(NSProcessInfo.processInfo.systemUptime * 1000000.0);
    BCHeaderVersion(header, self.protocolVersion, type, (uint32_t)payload.length, self.sequence++, now);
    NSMutableData *packet = [NSMutableData dataWithBytes:header length:sizeof(header)];
    [packet appendData:payload];
    // Destructor block retains packet until Network.framework has finished using its storage.
    dispatch_data_t data = dispatch_data_create(packet.bytes, packet.length, self.queue, ^{ (void)packet; });
    nw_connection_t connection = self.connection;
    self.pendingWrites++;
    [self.pendingWriteTimes addObject:@(NSProcessInfo.processInfo.systemUptime)];
    self.pendingHighWater = MAX(self.pendingHighWater, self.pendingWrites);
    __weak BCTransport *weakSelf = self;
    nw_connection_send(connection, data, NW_CONNECTION_DEFAULT_MESSAGE_CONTEXT, true, ^(nw_error_t error) {
        BCTransport *s = weakSelf;
        if (!s || s.connection != connection) return;
        s.pendingWrites--;
        if (s.pendingWriteTimes.count) {
            double elapsed = (NSProcessInfo.processInfo.systemUptime - s.pendingWriteTimes.firstObject.doubleValue) * 1000.0;
            [s.pendingWriteTimes removeObjectAtIndex:0];
            s.sendCompletions++;
            [s.sendCompletionSamples addObject:@(elapsed)];
        }
        if (error) [s disconnect:@"Disconnected — waiting for Windows"];
    });
}
- (void)updateTouches:(NSData *)bitmap
       eventTimestamp:(NSTimeInterval)eventTimestamp
      callbackStarted:(NSTimeInterval)callbackStarted
             contacts:(NSUInteger)contacts
         changedZones:(NSUInteger)changedZones {
    if (bitmap.length != 30) return;
    NSData *snapshot = [bitmap copy];
    dispatch_async(self.queue, ^{
        NSTimeInterval now = NSProcessInfo.processInfo.systemUptime;
        double callbackMs = (now - callbackStarted) * 1000.0;
        self.callbacks++;
        self.contactTotal += contacts;
        self.contactMax = MAX(self.contactMax, contacts);
        self.changedZoneTotal += changedZones;
        [self.eventToEnqueueSamples addObject:@(MAX(0, (now - eventTimestamp) * 1000.0))];
        [self.callbackToEnqueueSamples addObject:@(callbackMs)];
        if (!self.ready || [self.bitmap isEqualToData:snapshot]) return;
        self.bitmap = snapshot;
        [self send:BCTouch payload:snapshot];
    });
}
- (void)reportDiagnosticsIfDue {
    NSTimeInterval now = NSProcessInfo.processInfo.systemUptime;
    if (now - self.lastDiagnostics < 1.0) return;
    double oldestMs = self.pendingWriteTimes.count
        ? (now - self.pendingWriteTimes.firstObject.doubleValue) * 1000.0 : 0;
    NSLog(@"BCCA_DIAG callbacks=%lu contacts_avg=%.2f contacts_max=%lu changed_zones=%lu event_to_enqueue_ms[p50=%.3f,p95=%.3f,p99=%.3f,max=%.3f] callback_to_enqueue_ms[p50=%.3f,p95=%.3f,p99=%.3f,max=%.3f] pending=%lu pending_high_water=%lu oldest_pending_ms=%.3f send_completion_ms[p50=%.3f,p95=%.3f,p99=%.3f,max=%.3f]",
        (unsigned long)self.callbacks,
        self.callbacks ? (double)self.contactTotal / self.callbacks : 0,
        (unsigned long)self.contactMax, (unsigned long)self.changedZoneTotal,
        BCPercentile(self.eventToEnqueueSamples, .5), BCPercentile(self.eventToEnqueueSamples, .95),
        BCPercentile(self.eventToEnqueueSamples, .99), BCPercentile(self.eventToEnqueueSamples, 1),
        BCPercentile(self.callbackToEnqueueSamples, .5), BCPercentile(self.callbackToEnqueueSamples, .95),
        BCPercentile(self.callbackToEnqueueSamples, .99), BCPercentile(self.callbackToEnqueueSamples, 1),
        (unsigned long)self.pendingWrites,
        (unsigned long)self.pendingHighWater, oldestMs,
        BCPercentile(self.sendCompletionSamples, .5), BCPercentile(self.sendCompletionSamples, .95),
        BCPercentile(self.sendCompletionSamples, .99), BCPercentile(self.sendCompletionSamples, 1));
    self.callbacks = self.contactTotal = self.contactMax = self.changedZoneTotal = 0;
    self.pendingHighWater = self.pendingWrites;
    self.sendCompletions = 0;
    [self.eventToEnqueueSamples removeAllObjects];
    [self.callbackToEnqueueSamples removeAllObjects];
    [self.sendCompletionSamples removeAllObjects];
    self.lastDiagnostics = now;
}
- (void)disconnect:(NSString *)reason {
    if (self.timer) { dispatch_source_cancel(self.timer); self.timer = nil; }
    nw_connection_t connection = self.connection;
    self.connection = nil;
    self.ready = NO;
    self.videoSessionToken = nil;
    if (self.videoSessionChanged) self.videoSessionChanged(nil);
    self.received = nil;
    [self.pendingWriteTimes removeAllObjects];
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
