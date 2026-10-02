#import "BCVideoTransport.h"
#import "BCVideoDecoder.h"
#import "BCVideoWire.h"
#import "BCVideoJson.h"
#import <Network/Network.h>
#import <math.h>

typedef NS_ENUM(NSUInteger, BCVideoPhase) { BCVideoWaitingHello, BCVideoWaitingConfig, BCVideoConfiguring, BCVideoStreaming };
static BOOL BCNumber(id value, double minimum, double maximum, BOOL integer) {
    if (![value isKindOfClass:NSNumber.class] || CFGetTypeID((__bridge CFTypeRef)value)==CFBooleanGetTypeID()) return NO;
    double x=[value doubleValue]; return isfinite(x) && x>=minimum && x<=maximum && (!integer || floor(x)==x);
}
static BOOL BCArray(id value, NSUInteger count) {
    if (![value isKindOfClass:NSArray.class] || [value count]!=count) return NO;
    for (id number in value) if (!BCNumber(number,-16384,16384,NO)) return NO;
    return YES;
}
static BOOL BCConfig(NSDictionary *c) {
    if (![c[@"codec"] isEqual:@"h264"] || ![@[@"main",@"baseline"] containsObject:c[@"profile"]] ||
        !BCNumber(c[@"level"],1,42,YES) || !BCNumber(c[@"codedWidth"],2,1920,YES) || !BCNumber(c[@"codedHeight"],2,1920,YES) ||
        [c[@"codedWidth"] intValue]%2 || [c[@"codedHeight"] intValue]%2 || [c[@"codedWidth"] intValue]*[c[@"codedHeight"] intValue]>2073600 ||
        !BCNumber(c[@"fpsNum"],60,60,YES) || !BCNumber(c[@"fpsDen"],1,1,YES) || !BCNumber(c[@"nalLengthBytes"],4,4,YES) ||
        !BCNumber(c[@"bitrateBps"],4000000,20000000,YES) || !BCNumber(c[@"rotation"],0,0,YES) ||
        ![c[@"color"] isEqual:@"bt709-limited"] || !BCNumber(c[@"sourceWidth"],1,16384,YES) || !BCNumber(c[@"sourceHeight"],1,16384,YES) ||
        !BCArray(c[@"crop"],4) || !BCArray(c[@"contentRect"],4) || !BCArray(c[@"circle"],3)) return NO;
    NSArray *crop=c[@"crop"], *content=c[@"contentRect"], *circle=c[@"circle"];
    double x=[crop[0] doubleValue],y=[crop[1] doubleValue],w=[crop[2] doubleValue],h=[crop[3] doubleValue];
    if (x<0 || y<0 || w<=0 || h<=0 || x+w>1 || y+h>1) return NO;
    if ([content[0] doubleValue]<0 || [content[1] doubleValue]<0 || [content[2] doubleValue]<=0 || [content[3] doubleValue]<=0 ||
        [content[0] doubleValue]+[content[2] doubleValue]>[c[@"codedWidth"] doubleValue] ||
        [content[1] doubleValue]+[content[3] doubleValue]>[c[@"codedHeight"] doubleValue]) return NO;
    double cx=[circle[0] doubleValue],cy=[circle[1] doubleValue],r=[circle[2] doubleValue];
    double sw=[c[@"sourceWidth"] doubleValue],sh=[c[@"sourceHeight"] doubleValue];
    if (cx<0 || cx>1 || cy<0 || cy>1 || r<=0 || r>1 || cx-r*MIN(sw,sh)/sw<x || cx+r*MIN(sw,sh)/sw>x+w || cy-r*MIN(sw,sh)/sh<y || cy+r*MIN(sw,sh)/sh>y+h) return NO;
    if (![c[@"sps"] isKindOfClass:NSString.class] || ![c[@"pps"] isKindOfClass:NSString.class]) return NO;
    NSData *sps=[[NSData alloc] initWithBase64EncodedString:c[@"sps"] options:0],*pps=[[NSData alloc] initWithBase64EncodedString:c[@"pps"] options:0];
    if (sps.length<4 || sps.length>1024 || pps.length<1 || pps.length>1024) return NO;
    const uint8_t *s=sps.bytes,*p=pps.bytes;
    return !(s[0]&128) && !(p[0]&128) && (s[0]&31)==7 && (p[0]&31)==8 && s[1]==([c[@"profile"] isEqual:@"main"] ? 77 : 66) && s[3]==[c[@"level"] intValue];
}

@interface BCVideoTransport ()
@property(nonatomic, strong) dispatch_queue_t queue;
@property(nonatomic, strong) nw_listener_t listener;
@property(nonatomic, strong) nw_connection_t connection;
// Keep the stored property distinct from the public asynchronous setControlToken: method.
@property(nonatomic, strong) NSData *boundControlToken;
@property(nonatomic, strong) NSMutableData *received;
@property(nonatomic, strong) BCVideoDecoder *decoder;
@property(nonatomic, strong) dispatch_source_t timer;
@property(nonatomic, strong) NSMutableDictionary<NSNumber *,NSNumber *> *pending;
@property(nonatomic) BCVideoPhase phase;
@property(nonatomic) uint32_t incoming, outgoing;
@property(nonatomic) BOOL haveIncoming, haveIdr, failing;
@property(nonatomic) uint64_t generation, lastGeneration, lastReceived, lastDecoded, lastPresented, presentedAt;
@property(nonatomic) NSTimeInterval lastMessage, partialSince, phaseSince, lastReport;
@property(nonatomic) NSUInteger pendingWrites, decodedCount, receivedCount, presentedCount;
@end

@implementation BCVideoTransport
@synthesize videoView = _videoView;
- (instancetype)init {
    if ((self=[super init])) {
        _queue=dispatch_queue_create("org.brokencca.video.transport",DISPATCH_QUEUE_SERIAL); _decoder=[BCVideoDecoder new];
        _pending=[NSMutableDictionary dictionary];
        __weak BCVideoTransport *weakSelf=self;
        _decoder.decoded=^(CVPixelBufferRef buffer,uint64_t frame,uint64_t generation,BCVideoGeometry *geometry) {
            CVPixelBufferRetain(buffer);
            BCVideoTransport *s=weakSelf;
            if (!s) { CVPixelBufferRelease(buffer); return; }
            dispatch_async(s.queue, ^{
                if (s.connection && s.generation==generation && s.phase==BCVideoStreaming) {
                    [s.pending removeObjectForKey:@(frame)];
                    if (frame>s.lastDecoded) { s.lastDecoded=frame; s.decodedCount++; [s.videoView enqueueBuffer:buffer frame:frame generation:generation geometry:geometry]; }
                }
                CVPixelBufferRelease(buffer);
            });
        };
        _decoder.failed=^(NSString *reason,uint64_t generation) {
            BCVideoTransport *s=weakSelf; if (!s) return;
            dispatch_async(s.queue, ^{ if (s.connection && s.generation==generation) [s fail:reason]; });
        };
    }
    return self;
}
- (void)notify:(NSString *)status {
    dispatch_async(dispatch_get_main_queue(), ^{ if (self.statusChanged) self.statusChanged(status); });
}
- (void)setControlToken:(NSData *)token {
    NSData *copy=[token copy];
    dispatch_async(self.queue, ^{
        [self disconnect]; self.lastGeneration=0; self.boundControlToken=copy;
        NSLog(@"BCCA_VIDEO control_binding=%@", copy.length==16 ? @"ready" : @"cleared");
    });
}
- (void)start {
    dispatch_async(self.queue, ^{
        if (self.listener) return;
        nw_parameters_t params=nw_parameters_create_secure_tcp(NW_PARAMETERS_DISABLE_PROTOCOL,
            ^(nw_protocol_options_t options) { nw_tcp_options_set_no_delay(options,true); });
        nw_parameters_set_local_endpoint(params,nw_endpoint_create_host("127.0.0.1","24865"));
        nw_listener_t listener=nw_listener_create(params);
        if (!listener) { [self notify:@"Video listener unavailable"]; return; }
        self.listener=listener; nw_listener_set_queue(listener,self.queue);
        __weak BCVideoTransport *weakSelf=self;
        nw_listener_set_new_connection_handler(listener, ^(nw_connection_t connection) {
            BCVideoTransport *s=weakSelf;
            if (!s || s.listener!=listener) { nw_connection_cancel(connection); return; }
            if (s.connection || s.boundControlToken.length!=16) {
                NSString *reason=s.connection ? @"Another video connection is active" : @"Control video token is unavailable";
                NSLog(@"BCCA_VIDEO rejected_connection=%@",reason);
                [s notify:[@"Video unavailable · " stringByAppendingString:reason]];
                nw_connection_cancel(connection); return;
            }
            [s accept:connection];
        });
        nw_listener_set_state_changed_handler(listener, ^(nw_listener_state_t state,nw_error_t error) {
            BCVideoTransport *s=weakSelf; if (s.listener==listener && state==nw_listener_state_failed) [s stopInternal];
        });
        nw_listener_start(listener);
        self.timer=dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER,0,0,self.queue);
        dispatch_source_set_timer(self.timer,DISPATCH_TIME_NOW,50*NSEC_PER_MSEC,2*NSEC_PER_MSEC);
        dispatch_source_set_event_handler(self.timer, ^{ [weakSelf tick]; }); dispatch_resume(self.timer);
    });
}
- (void)accept:(nw_connection_t)connection {
    self.connection=connection; self.received=[NSMutableData data]; self.phase=BCVideoWaitingHello;
    self.generation=0; self.incoming=self.outgoing=0; self.haveIncoming=self.haveIdr=self.failing=NO;
    self.lastReceived=self.lastDecoded=self.lastPresented=self.presentedAt=0; self.pendingWrites=0;
    self.lastMessage=self.phaseSince=NSProcessInfo.processInfo.systemUptime; self.partialSince=0;
    [self.pending removeAllObjects];
    nw_connection_set_queue(connection,self.queue); __weak BCVideoTransport *weakSelf=self;
    nw_connection_set_state_changed_handler(connection, ^(nw_connection_state_t state,nw_error_t error) {
        BCVideoTransport *s=weakSelf; if (!s || s.connection!=connection) return;
        if (state==nw_connection_state_ready) [s receive:connection];
        if (state==nw_connection_state_failed || state==nw_connection_state_cancelled) [s disconnect];
    }); nw_connection_start(connection);
}
- (void)receive:(nw_connection_t)connection {
    __weak BCVideoTransport *weakSelf=self;
    nw_connection_receive(connection,1,65536,^(dispatch_data_t content,nw_content_context_t context,bool complete,nw_error_t error) {
        BCVideoTransport *s=weakSelf; if (!s || s.connection!=connection) return;
        if (content) {
            if (!s.received.length) s.partialSince=NSProcessInfo.processInfo.systemUptime;
            dispatch_data_apply(content,^bool(dispatch_data_t region,size_t offset,const void *bytes,size_t length) {
                [s.received appendBytes:bytes length:length]; return true;
            });
            if (s.received.length>BCVideoMaxAU+BCVideoHeaderSize+65536) { [s fail:@"Video receive buffer overflow"]; return; }
            [s parse];
        }
        if (s.connection!=connection) return;
        if (error || complete) { [s disconnect]; return; }
        [s receive:connection];
    });
}
- (void)parse {
    while (self.connection && !self.failing && self.received.length>=BCVideoHeaderSize) {
        const uint8_t *p=self.received.bytes;
        if (!BCValidVideoHeader(p)) { [self fail:@"Invalid video header"]; return; }
        NSUInteger total=BCVideoHeaderSize+BCGet32(p+8); if (self.received.length<total) return;
        uint8_t type=p[5]; BOOL idr=p[6]!=0; uint32_t sequence=BCGet32(p+12);
        uint64_t generation=BCGet64(p+16),frame=BCGet64(p+24),captured=BCGet64(p+32);
        if (self.haveIncoming && (int32_t)(sequence-self.incoming)<=0) { [self fail:@"Stale video sequence"]; return; }
        self.haveIncoming=YES; self.incoming=sequence;
        NSData *payload=[NSData dataWithBytes:p+BCVideoHeaderSize length:total-BCVideoHeaderSize];
        [self.received replaceBytesInRange:NSMakeRange(0,total) withBytes:NULL length:0];
        self.partialSince=self.received.length ? NSProcessInfo.processInfo.systemUptime : 0;
        self.lastMessage=NSProcessInfo.processInfo.systemUptime;
        NSDictionary *json=nil;
        if (type!=BCVideoAU) {
            if (!BCVideoJSONFields(payload.bytes,payload.length,type)) { [self fail:@"Invalid video JSON fields"]; return; }
            id parsed=[NSJSONSerialization JSONObjectWithData:payload options:0 error:nil];
            if (![parsed isKindOfClass:NSDictionary.class]) { [self fail:@"Malformed video JSON"]; return; } json=parsed;
        }
        if (self.phase==BCVideoWaitingHello) {
            if (type!=BCVideoHello || generation || !BCNumber(json[@"videoVersion"],1,1,YES) || ![json[@"sessionToken"] isKindOfClass:NSString.class]) { [self fail:@"Expected video HELLO"]; return; }
            NSMutableString *expected=[NSMutableString string]; const uint8_t *token=self.boundControlToken.bytes;
            for (NSUInteger i=0;i<self.boundControlToken.length;i++) [expected appendFormat:@"%02X",token[i]];
            if ([json[@"sessionToken"] length]!=32 || [expected caseInsensitiveCompare:json[@"sessionToken"]]!=NSOrderedSame) { [self fail:@"Video control token mismatch"]; return; }
            [self send:BCVideoHelloAck json:@{@"maxWidth":@1920,@"maxHeight":@1920,@"maxPixels":@2073600,@"maxFps":@60,@"maxAuBytes":@(BCVideoMaxAU),@"profiles":@[@"main",@"baseline"],@"maxLevel":@42}];
            self.phase=BCVideoWaitingConfig; self.phaseSince=self.lastMessage; continue;
        }
        if (self.phase==BCVideoWaitingConfig) {
            if (type!=BCVideoConfig || generation<=self.lastGeneration || !BCConfig(json) || !self.videoView.available) { [self fail:@"Unsupported video configuration or renderer"]; return; }
            self.generation=self.lastGeneration=generation; self.phase=BCVideoConfiguring; self.phaseSince=self.lastMessage;
            [self.videoView activateGeneration:generation]; nw_connection_t connection=self.connection;
            __weak BCVideoTransport *weakSelf=self;
            [self.decoder configure:json generation:generation completion:^(BOOL success,BOOL verified,NSString *reason) {
                BCVideoTransport *s=weakSelf; if (!s) return;
                dispatch_async(s.queue, ^{
                    if (s.connection!=connection || s.generation!=generation) return;
                    if (!success) { [s fail:reason]; return; }
                    s.phase=BCVideoStreaming; s.phaseSince=NSProcessInfo.processInfo.systemUptime;
                    [s send:BCVideoReady json:@{@"hardwareVerified":@(verified)}];
                    [s notify:verified ? @"Video connected · hardware H.264" : @"Video connected · hardware verification unavailable on this iOS"];
                });
            }]; continue;
        }
        if (self.phase!=BCVideoStreaming || generation!=self.generation) { [self fail:@"Unexpected video phase/generation"]; return; }
        if (type==BCVideoClockPing) {
            NSString *t1=json[@"t1Us"];
            if (![t1 isKindOfClass:NSString.class] || !t1.length || t1.length>20 ||
                [t1 rangeOfCharacterFromSet:NSCharacterSet.decimalDigitCharacterSet.invertedSet].location!=NSNotFound) { [self fail:@"Invalid clock ping"]; return; }
            uint64_t now=(uint64_t)(NSProcessInfo.processInfo.systemUptime*1000000.0);
            [self send:BCVideoClockPong json:@{@"t1Us":t1,@"t2Us":[NSString stringWithFormat:@"%llu",(unsigned long long)now],
                @"t3Us":[NSString stringWithFormat:@"%llu",(unsigned long long)(NSProcessInfo.processInfo.systemUptime*1000000.0)]}];
            continue;
        }
        if (type==BCVideoStatus) {
            if (![json[@"state"] isKindOfClass:NSString.class] || ![@[@"running",@"source-idle",@"paused",@"stopped"] containsObject:json[@"state"]] ||
                ![json[@"reason"] isKindOfClass:NSString.class] || [json[@"reason"] lengthOfBytesUsingEncoding:NSUTF8StringEncoding]>256) { [self fail:@"Invalid video status"]; return; }
            if ([json[@"state"] isEqual:@"paused"] || [json[@"state"] isEqual:@"stopped"]) [self notify:json[@"reason"]];
            continue;
        }
        if (type!=BCVideoAU || captured>INT64_MAX || frame<=self.lastReceived || !BCValidAvcc(payload.bytes,payload.length,idr) || (!self.haveIdr && !idr) || self.pending.count>=3) {
            [self fail:@"Invalid picture, reference, or decode backlog"]; return;
        }
        self.haveIdr=YES; self.lastReceived=frame; self.receivedCount++;
        self.pending[@(frame)]=@(NSProcessInfo.processInfo.systemUptime);
        [self.decoder decode:payload frame:frame capturedUs:captured generation:generation];
    }
}
- (void)send:(uint8_t)type json:(NSDictionary *)json {
    [self send:type json:json completion:nil];
}
- (void)send:(uint8_t)type json:(NSDictionary *)json completion:(void (^)(void))completion {
    if (!self.connection) return;
    if (self.pendingWrites>=4) { [self disconnect]; return; }
    NSData *payload=[NSJSONSerialization dataWithJSONObject:json options:0 error:nil];
    uint8_t header[BCVideoHeaderSize]; BCVideoHeader(header,type,false,(uint32_t)payload.length,self.outgoing++,type==BCVideoHelloAck ? 0 : self.generation,0,0);
    NSMutableData *packet=[NSMutableData dataWithBytes:header length:sizeof(header)]; [packet appendData:payload];
    dispatch_data_t data=dispatch_data_create(packet.bytes,packet.length,self.queue,^{ (void)packet; });
    nw_connection_t connection=self.connection; self.pendingWrites++; __weak BCVideoTransport *weakSelf=self;
    __block BOOL completed=NO;
    nw_connection_send(connection,data,NW_CONNECTION_DEFAULT_MESSAGE_CONTEXT,true,^(nw_error_t error) {
        completed=YES; BCVideoTransport *s=weakSelf; if (s.connection!=connection) return;
        s.pendingWrites--; if (error) [s disconnect]; else if (completion) completion();
    });
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW,100*NSEC_PER_MSEC),self.queue, ^{
        BCVideoTransport *s=weakSelf; if (!completed && s.connection==connection) [s disconnect];
    });
}
- (void)tick {
    if (!self.connection || self.failing) return;
    NSTimeInterval now=NSProcessInfo.processInfo.systemUptime;
    double timeout=self.phase==BCVideoStreaming ? 1.0 : 3.0;
    if (now-self.lastMessage>timeout || (self.partialSince && now-self.partialSince>timeout) ||
        (self.phase!=BCVideoStreaming && now-self.phaseSince>3.0) || (self.phase==BCVideoStreaming && !self.haveIdr && now-self.phaseSince>3.0)) {
        [self fail:@"Video timed out"]; return;
    }
    for (NSNumber *started in self.pending.allValues) if (now-started.doubleValue>0.05) { [self fail:@"Video decode queue too old"]; return; }
    if (self.phase!=BCVideoStreaming) return;
    NSString *thermal=@"nominal";
    switch (NSProcessInfo.processInfo.thermalState) {
        case NSProcessInfoThermalStateFair: thermal=@"fair"; break;
        case NSProcessInfoThermalStateSerious: thermal=@"serious"; break;
        case NSProcessInfoThermalStateCritical: thermal=@"critical"; break;
        default: break;
    }
    NSDictionary *stats=[self.videoView statistics];
    [self send:BCVideoFeedback json:@{@"receivedId":[NSString stringWithFormat:@"%llu",(unsigned long long)self.lastReceived],
        @"decodedId":[NSString stringWithFormat:@"%llu",(unsigned long long)self.lastDecoded],@"presentedId":[NSString stringWithFormat:@"%llu",(unsigned long long)self.lastPresented],
        @"presentedAtUs":[NSString stringWithFormat:@"%llu",(unsigned long long)self.presentedAt],@"pendingDecode":@(self.pending.count),@"replacedDecoded":stats[@"replacedDecoded"] ?: @0,@"thermal":thermal,@"displayMilliHz":stats[@"displayMilliHz"] ?: @0}];
    if (now-self.lastReport>=1) {
        NSLog(@"BCCA_VIDEO received=%lu decoded=%lu presented=%lu pending=%lu generation=%llu thermal=%@ display_millihz=%@ low_power=%d render_max_ms=%.3f drawable_wait_max_ms=%.3f",(unsigned long)self.receivedCount,
            (unsigned long)self.decodedCount,(unsigned long)self.presentedCount,(unsigned long)self.pending.count,(unsigned long long)self.generation,thermal,
            stats[@"displayMilliHz"],NSProcessInfo.processInfo.lowPowerModeEnabled,[stats[@"maxRenderMs"] doubleValue],[stats[@"maxDrawableWaitMs"] doubleValue]);
        self.receivedCount=self.decodedCount=self.presentedCount=0; self.lastReport=now;
    }
}
- (void)setVideoView:(BCVideoView *)view {
    _videoView=view; __weak BCVideoTransport *weakSelf=self;
    view.presented=^(uint64_t frame,uint64_t generation,uint64_t timeUs) {
        BCVideoTransport *s=weakSelf; if (!s) return;
        dispatch_async(s.queue, ^{ if (s.generation==generation && frame>s.lastPresented) { s.lastPresented=frame; s.presentedAt=timeUs; s.presentedCount++; } });
    };
}
- (void)fail:(NSString *)reason {
    if (self.failing) return; self.failing=YES;
    [self notify:[@"Video paused · " stringByAppendingString:reason ?: @"error"]];
    nw_connection_t connection=self.connection;
    __weak BCVideoTransport *weakSelf=self;
    [self send:BCVideoError json:@{@"code":@"invalid-stream",@"detail":reason ?: @"Video failed"} completion:^{
        BCVideoTransport *s=weakSelf; if (s.connection==connection) [s disconnect];
    }];
}
- (void)disconnect {
    nw_connection_t connection=self.connection; self.connection=nil; self.generation=0; self.received=nil;
    [self.pending removeAllObjects]; [self.videoView clear]; [self.decoder stop];
    if (connection) { nw_connection_set_state_changed_handler(connection,NULL); nw_connection_cancel(connection); }
}
- (void)stopInternal {
    [self disconnect]; self.boundControlToken=nil;
    if (self.timer) { dispatch_source_cancel(self.timer); self.timer=nil; }
    if (self.listener) { nw_listener_set_state_changed_handler(self.listener,NULL); nw_listener_set_new_connection_handler(self.listener,NULL); nw_listener_cancel(self.listener); self.listener=nil; }
}
- (void)stop { dispatch_async(self.queue, ^{ [self stopInternal]; }); }
@end
