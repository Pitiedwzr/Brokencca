#import "BCVideoView.h"
#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>
#import <QuartzCore/CADisplayLink.h>
#import <math.h>
#import <simd/simd.h>
#import "BCVideoSchedule.h"

@interface BCDisplayTarget : NSObject
@property(nonatomic, weak) BCVideoView *view;
- (void)tick:(CADisplayLink *)link;
@end

@interface BCVideoView () {
    CVMetalTextureCacheRef _cache;
    CVPixelBufferRef _latest;
    CVPixelBufferRef _displayedBuffer;
    uint64_t _latestFrame, _latestGeneration, _generation, _revision;
    BCVideoSchedule _schedule;
    uint32_t _replaced, _displayMilliHz;
    CFTimeInterval _lastTick;
    double _maxDrawableWaitMs, _maxRenderMs;
    double _drawableWaitTotalMs, _renderTotalMs, _gpuTotalMs, _maxGpuMs;
    uint32_t _renderCount, _gpuCount;
}
@property(nonatomic, strong) NSLock *guard;
@property(nonatomic, strong) BCVideoGeometry *latestGeometry;
@property(nonatomic, strong) BCVideoTiming *latestTiming;
@property(nonatomic, strong) BCVideoTiming *displayedTiming;
@property(nonatomic, strong) NSDictionary *displayedConfiguration;
@property(nonatomic, strong) id<MTLDevice> device;
@property(nonatomic, strong) id<MTLCommandQueue> commandQueue;
@property(nonatomic, strong) id<MTLRenderPipelineState> pipeline;
@property(nonatomic, strong) CADisplayLink *displayLink;
@property(nonatomic, strong) BCDisplayTarget *displayTarget;
- (void)drawFrame:(CADisplayLink *)link;
- (void)renderLatestFrame;
- (void)requestRender;
@end

@implementation BCDisplayTarget
- (void)tick:(CADisplayLink *)link { [self.view drawFrame:link]; }
@end

@implementation BCVideoView
@synthesize zoomToPlayfield = _zoomToPlayfield;
+ (Class)layerClass { return CAMetalLayer.class; }
- (instancetype)initWithFrame:(CGRect)frame {
    if ((self = [super initWithFrame:frame])) {
        self.userInteractionEnabled = NO; self.backgroundColor = UIColor.blackColor;
        _guard = [NSLock new]; _device = MTLCreateSystemDefaultDevice();
        if (!_device) return self;
        CAMetalLayer *layer = (CAMetalLayer *)self.layer;
        layer.device = _device; layer.pixelFormat = MTLPixelFormatBGRA8Unorm; layer.framebufferOnly = YES;
        // Core Animation can still own the displayed drawable after GPU completion.
        // Keep a third drawable available, while retaining the two-submission limit.
        layer.maximumDrawableCount = 3; layer.presentsWithTransaction = NO;
        CGColorSpaceRef space = CGColorSpaceCreateWithName(kCGColorSpaceITUR_709); layer.colorspace = space; CGColorSpaceRelease(space);
        _commandQueue = [_device newCommandQueue];
        NSString *source = @"#include <metal_stdlib>\nusing namespace metal;\n"
            "struct V { float4 p [[position]]; float2 uv; };\n"
            "vertex V vs(uint id [[vertex_id]]) { V v; v.uv=float2((id<<1)&2,id&2); v.p=float4(v.uv*float2(2,-2)+float2(-1,1),0,1); return v; }\n"
            "fragment float4 ps(V v [[stage_in]], texture2d<float> y [[texture(0)]], texture2d<float> uv [[texture(1)]], constant float4 &region [[buffer(0)]]) {\n"
            "constexpr sampler s(coord::normalized,filter::linear,address::clamp_to_edge);\n"
            "float2 p=region.xy+v.uv*region.zw; if(any(p<0.0)||any(p>1.0)) return float4(0,0,0,1);\n"
            "float l=(y.sample(s,p).r-16.0/255.0)*(255.0/219.0);\n"
            "float2 c=(uv.sample(s,p).rg-float2(128.0/255.0))*(255.0/224.0);\n"
            "return float4(clamp(float3(l+1.5748*c.y,l-0.187324*c.x-0.468124*c.y,l+1.8556*c.x),0.0,1.0),1); }\n";
        NSError *error = nil; id<MTLLibrary> library = [_device newLibraryWithSource:source options:nil error:&error];
        MTLRenderPipelineDescriptor *desc = [MTLRenderPipelineDescriptor new];
        desc.vertexFunction = [library newFunctionWithName:@"vs"]; desc.fragmentFunction = [library newFunctionWithName:@"ps"];
        desc.colorAttachments[0].pixelFormat = layer.pixelFormat;
        if (library) _pipeline = [_device newRenderPipelineStateWithDescriptor:desc error:&error];
        if (!_pipeline || CVMetalTextureCacheCreate(kCFAllocatorDefault, NULL, _device, NULL, &_cache) != kCVReturnSuccess) { NSLog(@"Video Metal setup failed: %@", error); return self; }
        _displayTarget = [BCDisplayTarget new]; _displayTarget.view = self;
        _displayLink = [CADisplayLink displayLinkWithTarget:_displayTarget selector:@selector(tick:)];
        _displayLink.preferredFrameRateRange = CAFrameRateRangeMake(60,60,60);
        [_displayLink addToRunLoop:NSRunLoop.mainRunLoop forMode:NSRunLoopCommonModes];
    }
    return self;
}
- (BOOL)available { return self.pipeline != nil && _cache != NULL; }
- (void)layoutSubviews {
    [super layoutSubviews]; CGFloat scale = self.window.screen.scale ?: UIScreen.mainScreen.scale;
    ((CAMetalLayer *)self.layer).drawableSize = CGSizeMake(self.bounds.size.width*scale,self.bounds.size.height*scale);
    // Re-render a static source after rotation; changing only layer bounds can stretch
    // the previous drawable and disagree with the newly computed touch transform.
    [self.guard lock];
    if (!_latest && _displayedBuffer && _latestGeneration == _generation && _generation) {
        _latest = CVPixelBufferRetain(_displayedBuffer);
        self.latestTiming=[self.displayedTiming renderCopy];
    }
    [self.guard unlock];
    [self requestRender];
}
- (void)activateGeneration:(uint64_t)generation {
    BCVideoTiming *discarded;
    [self.guard lock]; _generation = generation; _revision++; _latestFrame = 0; _replaced = 0;
    _maxDrawableWaitMs = _maxRenderMs = 0;
    _drawableWaitTotalMs = _renderTotalMs = _gpuTotalMs = _maxGpuMs = 0;
    _renderCount = _gpuCount = 0;
    discarded=_latest ? self.latestTiming : nil;
    if (_latest) { CVPixelBufferRelease(_latest); _latest = NULL; }
    if (_displayedBuffer) { CVPixelBufferRelease(_displayedBuffer); _displayedBuffer = NULL; }
    self.latestGeometry = nil; self.latestTiming=nil; self.displayedTiming=nil; [self.guard unlock];
    [discarded dropped:@"generation-retired"];
}
- (void)enqueueBuffer:(CVPixelBufferRef)buffer frame:(uint64_t)frame generation:(uint64_t)generation geometry:(BCVideoGeometry *)geometry timing:(BCVideoTiming *)timing {
    BCVideoTiming *discarded=nil;
    [self.guard lock];
    if (generation == _generation && generation != 0 && frame > _latestFrame) {
        if (_latest) { discarded=self.latestTiming; CVPixelBufferRelease(_latest); if (_replaced < UINT32_MAX) _replaced++; }
        _latest = CVPixelBufferRetain(buffer); _latestFrame = frame; _latestGeneration = generation; self.latestGeometry = geometry;
        self.latestTiming=timing;
    }
    [self.guard unlock];
    [self requestRender];
    [discarded dropped:@"replaced"];
}
- (void)setPaused:(BOOL)paused {
    [self.guard lock]; _schedule.paused=paused; [self.guard unlock];
    dispatch_async(dispatch_get_main_queue(), ^{ self.displayLink.paused = paused; [self requestRender]; });
}
- (void)setZoomToPlayfield:(BOOL)zoom {
    if (_zoomToPlayfield == zoom) return;
    _zoomToPlayfield = zoom;
    // Publish the new touch geometry only when the matching image is rendered.
    // If video is disconnected, keep the frozen image and its mapping aligned.
    self.displayedConfiguration = nil;
    [self.guard lock];
    if (!_latest && _displayedBuffer && _latestGeneration == _generation && _generation) {
        _latest = CVPixelBufferRetain(_displayedBuffer);
        self.latestTiming=[self.displayedTiming renderCopy];
    }
    [self.guard unlock];
    [self requestRender];
}
- (void)clear { [self activateGeneration:0]; }
- (BOOL)isGenerationActive:(uint64_t)generation revision:(uint64_t)revision {
    [self.guard lock]; BOOL current=generation && generation==_generation && revision==_revision;
    [self.guard unlock]; return current;
}
- (NSDictionary *)statistics {
    [self.guard lock]; NSDictionary *result = @{@"replacedDecoded":@(_replaced),@"displayMilliHz":@(_displayMilliHz),
        @"maxDrawableWaitMs":@(_maxDrawableWaitMs),@"maxRenderMs":@(_maxRenderMs),
        @"renderCount":@(_renderCount),@"meanDrawableWaitMs":@(_renderCount ? _drawableWaitTotalMs/_renderCount : 0),
        @"meanRenderMs":@(_renderCount ? _renderTotalMs/_renderCount : 0),
        @"gpuCount":@(_gpuCount),@"meanGpuMs":@(_gpuCount ? _gpuTotalMs/_gpuCount : 0),@"maxGpuMs":@(_maxGpuMs)};
    [self.guard unlock]; return result;
}
- (void)drawFrame:(CADisplayLink *)link {
    [self.guard lock];
    if (_lastTick && link.timestamp > _lastTick) _displayMilliHz = (uint32_t)MIN(240000,round(1000.0/(link.timestamp-_lastTick)));
    _lastTick = link.timestamp;
    [self.guard unlock];
    // Decode/presentation callbacks request rendering immediately. The display
    // link supplies a fallback and measures cadence, without gating new frames.
    [self requestRender];
}
- (void)requestRender {
    [self.guard lock];
    BOOL requested=BCVideoRequestRender(&_schedule,_latest && _generation);
    [self.guard unlock];
    if (!requested) return;
    __weak BCVideoView *weakSelf=self;
    dispatch_async(dispatch_get_main_queue(), ^{
        @autoreleasepool { [weakSelf renderLatestFrame]; }
    });
}
- (void)renderLatestFrame {
    BOOL drawableBounds=self.available && self.bounds.size.width>0 && self.bounds.size.height>0;
    CFTimeInterval renderStarted = CACurrentMediaTime();
    [self.guard lock];
    if (!BCVideoBeginRender(&_schedule,drawableBounds && _latest && _generation)) { [self.guard unlock]; return; }
    __block BCVideoRenderTicket ticket={0};
    CVPixelBufferRef buffer = _latest; _latest = NULL;
    if (_displayedBuffer) CVPixelBufferRelease(_displayedBuffer);
    _displayedBuffer = CVPixelBufferRetain(buffer);
    uint64_t frame = _latestFrame, generation = _latestGeneration, revision = _revision; BCVideoGeometry *geometry = self.latestGeometry;
    BCVideoTiming *timing=[self.latestTiming renderCopy]; timing.renderStartedUs=BCVideoNowUs(); self.displayedTiming=timing;
    [self.guard unlock];
    geometry = [[BCVideoGeometry alloc] initWithConfiguration:geometry.configuration zoomToPlayfield:self.zoomToPlayfield];
    CVMetalTextureRef y = NULL, uv = NULL;
    CVReturn a = CVMetalTextureCacheCreateTextureFromImage(kCFAllocatorDefault,_cache,buffer,NULL,MTLPixelFormatR8Unorm,
        CVPixelBufferGetWidthOfPlane(buffer,0),CVPixelBufferGetHeightOfPlane(buffer,0),0,&y);
    CVReturn b = CVMetalTextureCacheCreateTextureFromImage(kCFAllocatorDefault,_cache,buffer,NULL,MTLPixelFormatRG8Unorm,
        CVPixelBufferGetWidthOfPlane(buffer,1),CVPixelBufferGetHeightOfPlane(buffer,1),1,&uv);
    id<CAMetalDrawable> drawable = nil;
    CFTimeInterval drawableStarted = CACurrentMediaTime();
    if (a == kCVReturnSuccess && b == kCVReturnSuccess) drawable = [(CAMetalLayer *)self.layer nextDrawable];
    double drawableWaitMs = (CACurrentMediaTime()-drawableStarted)*1000;
    [self.guard lock];
    BOOL current = generation == _generation && revision == _revision;
    if (current) _maxDrawableWaitMs = MAX(_maxDrawableWaitMs,drawableWaitMs);
    [self.guard unlock];
    if (!drawable || !current) {
        [timing dropped:current ? @"drawable-unavailable" : @"generation-retired"];
        if (y) CFRelease(y); if (uv) CFRelease(uv); CVPixelBufferRelease(buffer);
        [self.guard lock]; BCVideoGPUFinished(&_schedule,&ticket,true); [self.guard unlock]; return;
    }
    NSMutableDictionary *mapping = [[geometry.configuration dictionaryWithValuesForKeys:@[@"codedWidth",@"codedHeight",@"sourceWidth",@"sourceHeight",@"crop",@"contentRect",@"circle"]] mutableCopy];
    mapping[@"zoomToPlayfield"] = @(self.zoomToPlayfield);
    // Control reconnects can reuse a generation number and identical calibration.
    // Republish geometry then, and reject presentation feedback from the old session.
    mapping[@"rendererRevision"] = @(revision);
    if (![self.displayedConfiguration isEqual:mapping]) {
        self.displayedConfiguration = mapping;
        if (self.geometryChanged) self.geometryChanged(geometry);
    }
    MTLRenderPassDescriptor *pass = [MTLRenderPassDescriptor renderPassDescriptor];
    pass.colorAttachments[0].texture = drawable.texture; pass.colorAttachments[0].loadAction = MTLLoadActionClear;
    pass.colorAttachments[0].storeAction = MTLStoreActionStore; pass.colorAttachments[0].clearColor = MTLClearColorMake(0,0,0,1);
    id<MTLCommandBuffer> commands = [self.commandQueue commandBuffer];
    id<MTLRenderCommandEncoder> encoder = [commands renderCommandEncoderWithDescriptor:pass];
    if (!commands || !encoder) {
        [timing dropped:@"render-unavailable"]; CFRelease(y); CFRelease(uv); CVPixelBufferRelease(buffer);
        [self.guard lock]; BCVideoGPUFinished(&_schedule,&ticket,true); [self.guard unlock]; return;
    }
    [encoder setRenderPipelineState:self.pipeline];
    CGRect fit = [geometry codedRectInBounds:self.bounds];
    // Crop in UV coordinates rather than an oversized/negative Metal viewport.
    // The same coded rectangle drives the inverse touch and ring overlay mapping.
    vector_float4 region = {(float)((self.bounds.origin.x-fit.origin.x)/fit.size.width),
        (float)((self.bounds.origin.y-fit.origin.y)/fit.size.height),
        (float)(self.bounds.size.width/fit.size.width),(float)(self.bounds.size.height/fit.size.height)};
    [encoder setViewport:(MTLViewport){0,0,drawable.texture.width,drawable.texture.height,0,1}];
    [encoder setFragmentBytes:&region length:sizeof(region) atIndex:0];
    [encoder setFragmentTexture:CVMetalTextureGetTexture(y) atIndex:0];
    [encoder setFragmentTexture:CVMetalTextureGetTexture(uv) atIndex:1];
    [encoder drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3]; [encoder endEncoding];
    __weak BCVideoView *weakSelf = self;
    [drawable addPresentedHandler:^(id<MTLDrawable> shown) {
        BCVideoView *s = weakSelf;
        [s.guard lock];
        BOOL current=NO;
        if (s) { BCVideoPresentationFinished(&s->_schedule,&ticket); current=generation==s->_generation && revision==s->_revision; }
        [s.guard unlock];
        if (current && shown.presentedTime>0 && s.presented) s.presented(frame,generation,(uint64_t)(shown.presentedTime*1000000.0),revision);
        [s requestRender];
        [timing presented:(uint64_t)(shown.presentedTime*1000000.0)];
    }];
    [commands addCompletedHandler:^(id<MTLCommandBuffer> finished) {
        uint64_t completedUs=BCVideoNowUs();
        CFRelease(y); CFRelease(uv); CVPixelBufferRelease(buffer);
        BCVideoView *s = weakSelf; [s.guard lock];
        if (s) {
            BCVideoGPUFinished(&s->_schedule,&ticket,finished.status==MTLCommandBufferStatusError);
            if (generation == s->_generation && revision == s->_revision && finished.GPUEndTime > finished.GPUStartTime) {
                double gpuMs = (finished.GPUEndTime-finished.GPUStartTime)*1000;
                s->_gpuCount++; s->_gpuTotalMs += gpuMs; s->_maxGpuMs = MAX(s->_maxGpuMs,gpuMs);
            }
        }
        [s.guard unlock];
        [s requestRender];
        [timing gpuStarted:(uint64_t)(finished.GPUStartTime*1000000.0) ended:(uint64_t)(finished.GPUEndTime*1000000.0)
            completed:completedUs failed:finished.status==MTLCommandBufferStatusError];
    }];
    [commands presentDrawable:drawable]; timing.committedUs=BCVideoNowUs(); [commands commit];
    double renderMs = (CACurrentMediaTime()-renderStarted)*1000;
    [self.guard lock];
    if (generation == _generation && revision == _revision) {
        _renderCount++; _drawableWaitTotalMs += drawableWaitMs; _renderTotalMs += renderMs;
        _maxRenderMs = MAX(_maxRenderMs,renderMs);
    }
    [self.guard unlock];
}
- (void)dealloc {
    [self.displayLink invalidate]; if (_latest) CVPixelBufferRelease(_latest); if (_displayedBuffer) CVPixelBufferRelease(_displayedBuffer); if (_cache) CFRelease(_cache);
}
@end
