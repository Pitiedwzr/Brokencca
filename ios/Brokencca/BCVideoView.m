#import "BCVideoView.h"
#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>
#import <QuartzCore/CADisplayLink.h>
#import <math.h>
#import <simd/simd.h>

@interface BCDisplayTarget : NSObject
@property(nonatomic, weak) BCVideoView *view;
- (void)tick:(CADisplayLink *)link;
@end

@interface BCVideoView () {
    CVMetalTextureCacheRef _cache;
    CVPixelBufferRef _latest;
    CVPixelBufferRef _displayedBuffer;
    uint64_t _latestFrame, _latestGeneration, _generation;
    NSUInteger _inflight;
    uint32_t _replaced, _displayMilliHz;
    CFTimeInterval _lastTick;
    double _maxDrawableWaitMs, _maxRenderMs;
}
@property(nonatomic, strong) NSLock *guard;
@property(nonatomic, strong) BCVideoGeometry *latestGeometry;
@property(nonatomic, strong) NSDictionary *displayedConfiguration;
@property(nonatomic, strong) id<MTLDevice> device;
@property(nonatomic, strong) id<MTLCommandQueue> commandQueue;
@property(nonatomic, strong) id<MTLRenderPipelineState> pipeline;
@property(nonatomic, strong) CADisplayLink *displayLink;
@property(nonatomic, strong) BCDisplayTarget *displayTarget;
- (void)drawFrame:(CADisplayLink *)link;
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
        layer.maximumDrawableCount = 2;
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
    if (!_latest && _displayedBuffer && _latestGeneration == _generation && _generation)
        _latest = CVPixelBufferRetain(_displayedBuffer);
    [self.guard unlock];
}
- (void)activateGeneration:(uint64_t)generation {
    [self.guard lock]; _generation = generation; _latestFrame = 0; _replaced = 0;
    _maxDrawableWaitMs = _maxRenderMs = 0;
    if (_latest) { CVPixelBufferRelease(_latest); _latest = NULL; }
    if (_displayedBuffer) { CVPixelBufferRelease(_displayedBuffer); _displayedBuffer = NULL; }
    self.latestGeometry = nil; [self.guard unlock];
}
- (void)enqueueBuffer:(CVPixelBufferRef)buffer frame:(uint64_t)frame generation:(uint64_t)generation geometry:(BCVideoGeometry *)geometry {
    [self.guard lock];
    if (generation == _generation && generation != 0 && frame > _latestFrame) {
        if (_latest) { CVPixelBufferRelease(_latest); if (_replaced < UINT32_MAX) _replaced++; }
        _latest = CVPixelBufferRetain(buffer); _latestFrame = frame; _latestGeneration = generation; self.latestGeometry = geometry;
    }
    [self.guard unlock];
}
- (void)setPaused:(BOOL)paused { dispatch_async(dispatch_get_main_queue(), ^{ self.displayLink.paused = paused; }); }
- (void)setZoomToPlayfield:(BOOL)zoom {
    if (_zoomToPlayfield == zoom) return;
    _zoomToPlayfield = zoom;
    // Publish the new touch geometry only when the matching image is rendered.
    // If video is disconnected, keep the frozen image and its mapping aligned.
    self.displayedConfiguration = nil;
    [self.guard lock];
    if (!_latest && _displayedBuffer && _latestGeneration == _generation && _generation)
        _latest = CVPixelBufferRetain(_displayedBuffer);
    [self.guard unlock];
}
- (void)clear { [self activateGeneration:0]; }
- (NSDictionary *)statistics {
    [self.guard lock]; NSDictionary *result = @{@"replacedDecoded":@(_replaced),@"displayMilliHz":@(_displayMilliHz),
        @"maxDrawableWaitMs":@(_maxDrawableWaitMs),@"maxRenderMs":@(_maxRenderMs)};
    [self.guard unlock]; return result;
}
- (void)drawFrame:(CADisplayLink *)link {
    [self.guard lock];
    if (_lastTick && link.timestamp > _lastTick) _displayMilliHz = (uint32_t)MIN(240000,round(1000.0/(link.timestamp-_lastTick)));
    _lastTick = link.timestamp;
    [self.guard unlock];
    if (!self.available || self.bounds.size.width <= 0 || self.bounds.size.height <= 0) return;
    CFTimeInterval renderStarted = CACurrentMediaTime();
    [self.guard lock];
    if (!_latest || _inflight >= 2) { [self.guard unlock]; return; }
    CVPixelBufferRef buffer = _latest; _latest = NULL;
    if (_displayedBuffer) CVPixelBufferRelease(_displayedBuffer);
    _displayedBuffer = CVPixelBufferRetain(buffer);
    uint64_t frame = _latestFrame, generation = _latestGeneration; BCVideoGeometry *geometry = self.latestGeometry;
    _inflight++; [self.guard unlock];
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
    [self.guard lock]; _maxDrawableWaitMs = MAX(_maxDrawableWaitMs,drawableWaitMs); [self.guard unlock];
    if (!drawable) {
        if (y) CFRelease(y); if (uv) CFRelease(uv); CVPixelBufferRelease(buffer);
        [self.guard lock]; _inflight--; [self.guard unlock]; return;
    }
    NSMutableDictionary *mapping = [[geometry.configuration dictionaryWithValuesForKeys:@[@"codedWidth",@"codedHeight",@"sourceWidth",@"sourceHeight",@"crop",@"contentRect",@"circle"]] mutableCopy];
    mapping[@"zoomToPlayfield"] = @(self.zoomToPlayfield);
    if (![self.displayedConfiguration isEqual:mapping]) {
        self.displayedConfiguration = mapping;
        if (self.geometryChanged) self.geometryChanged(geometry);
    }
    MTLRenderPassDescriptor *pass = [MTLRenderPassDescriptor renderPassDescriptor];
    pass.colorAttachments[0].texture = drawable.texture; pass.colorAttachments[0].loadAction = MTLLoadActionClear;
    pass.colorAttachments[0].storeAction = MTLStoreActionStore; pass.colorAttachments[0].clearColor = MTLClearColorMake(0,0,0,1);
    id<MTLCommandBuffer> commands = [self.commandQueue commandBuffer];
    id<MTLRenderCommandEncoder> encoder = [commands renderCommandEncoderWithDescriptor:pass];
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
        if (s.presented) s.presented(frame,generation,(uint64_t)(shown.presentedTime*1000000.0));
    }];
    [commands addCompletedHandler:^(id<MTLCommandBuffer> finished) {
        CFRelease(y); CFRelease(uv); CVPixelBufferRelease(buffer);
        BCVideoView *s = weakSelf; [s.guard lock]; if (s) s->_inflight--; [s.guard unlock];
    }];
    [commands presentDrawable:drawable]; [commands commit];
    [self.guard lock]; _maxRenderMs = MAX(_maxRenderMs,(CACurrentMediaTime()-renderStarted)*1000); [self.guard unlock];
}
- (void)dealloc {
    [self.displayLink invalidate]; if (_latest) CVPixelBufferRelease(_latest); if (_displayedBuffer) CVPixelBufferRelease(_displayedBuffer); if (_cache) CFRelease(_cache);
}
@end
